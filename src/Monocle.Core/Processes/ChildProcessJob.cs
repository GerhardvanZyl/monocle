using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Monocle.Core.Processes;

/// <summary>
/// A Windows Job Object that kills every assigned child process when this app's process ends — for
/// ANY reason (clean close, crash, taskkill, debugger stop). The OS guarantees the teardown, so the
/// helpers the app spawns (the Python sidecar, the llama.cpp GPU server, the Claude CLI and the MCP
/// server that CLI launches as a grandchild) can never outlive the app as orphans holding VRAM or a
/// file lock. <see cref="Cleanup"/> already handles a normal close; this is the safety net for the
/// paths Cleanup never runs on. No-op on non-Windows — <see cref="Assign"/> just does nothing there.
/// </summary>
public static class ChildProcessJob
{
    private static readonly object Gate = new();
    private static IntPtr _job = IntPtr.Zero;
    private static bool _init;

    /// <summary>Add a freshly-started process to the kill-on-exit job. Assign right after Process.Start
    /// so any grandchildren it spawns later inherit the job too (Claude → MCP server). Best-effort:
    /// silently does nothing off Windows or if the job couldn't be created.</summary>
    public static void Assign(Process process)
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            var job = EnsureJob();
            if (job != IntPtr.Zero && !process.HasExited)
                AssignProcessToJobObject(job, process.Handle);
        }
        catch { /* a child we couldn't pin is still killed by the normal Cleanup()/Kill() path */ }
    }

    private static IntPtr EnsureJob()
    {
        lock (Gate)
        {
            if (_init)
                return _job;
            _init = true;

            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
                return _job = IntPtr.Zero;

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
            };
            int len = Marshal.SizeOf(info);
            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                Marshal.StructureToPtr(info, buf, false);
                SetInformationJobObject(job, JobObjectExtendedLimitInformation, buf, (uint)len);
            }
            finally { Marshal.FreeHGlobal(buf); }

            // Deliberately never closed: KILL_ON_JOB_CLOSE fires when the last handle to the job closes,
            // and we want that to be exactly when this process dies. The OS closes it on exit, killing
            // the whole tree then — earlier would kill the children while the app is still using them.
            return _job = job;
        }
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpInfo, uint cbInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
