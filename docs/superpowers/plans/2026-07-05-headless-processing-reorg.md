# Headless launch, per-model verdicts, Scan/Process reorg — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Monocle launch headlessly from an exe, keep every model's verdict instead of clobbering, surface Qwen, and split the folder workflow into a deterministic **Scan** and a probabilistic **Process** driven from a left-rail AI Cull section.

**Architecture:** Claude becomes a set of pseudo-`IModelRunner`s (one per model) so it lives in the same model checklist and per-model `scores` cache as every other scorer — this is what makes verdicts coexist and reuses the existing `(photoId, modelId)` keying. Scan drops all scorers; a new Process command runs the ticked scoring runners (per-photo) plus a folder-level Claude cull per ticked Claude model. The MCP server is launched via its windowless `WinExe` apphost so no console flashes.

**Tech Stack:** C# / .NET 10, Avalonia 11 MVVM (CommunityToolkit.Mvvm), xUnit, SQLite (`ShootCache`).

## Global Constraints

- Target framework: **net10.0** for all projects (verbatim from CLAUDE.md).
- Build/test with the user-local SDK: `$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"`; run with `$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"`.
- Tests live only in `tests/Monocle.Core.Tests` (xUnit, `namespace Monocle.Core.Tests;`). There is **no UI test project** — App/XAML changes are verified manually (repo convention).
- A single runner throwing must stay swallowed (graceful degrade in `ShootService.AnalyzeAsync`) — do not remove this.
- Sidecar/On1 contract unchanged: only `<name>.xmp` + `<name>.txt`, never `.on1`; the XMP star stays a single value (last-writer-wins is intentional here).
- Claude model ids are taken verbatim from existing code — do **not** invent new ones: `claude-haiku-4-5`, `claude-sonnet-4-6`, `claude-opus-4-8` (`MainWindowViewModel.cs:427`).
- Allowed-tools list in `MonocleTools.All` must stay in sync with `Monocle.Mcp/PhotoTools.cs` — this plan does not add MCP tools, so leave it untouched.

---

## Task 1: Launch the MCP server via the windowless apphost (A1 — kill the cull console flash)

**Files:**
- Modify: `src/Monocle.App/Services/CullLauncher.cs`
- Test: `tests/Monocle.Core.Tests/CullLauncherTests.cs` (create)

**Why:** During a cull, `claude.exe` (Node) spawns the MCP server from `.mcp.json`. Today the config runs `dotnet.exe` (a console-subsystem muxer) → Windows allocates a console → flash. `Monocle.Mcp` already builds as `WinExe`, producing a windowless `Monocle.Mcp.exe` copied to `mcp/`. Point the config at that exe, no args, no `dotnet` host.

**Interfaces:**
- Produces: `CullLauncher.McpServerExe()` → `string` (path to `mcp/Monocle.Mcp.exe`), `CullLauncher.McpServerExists()` → `bool` (checks the exe), `CullLauncher.WriteMcpConfig()` → `string` (unchanged signature, now writes `command = <exe>`, `args = []`, no `env`).

- [ ] **Step 1: Write the failing test**

Create `tests/Monocle.Core.Tests/CullLauncherTests.cs`. `CullLauncher` is in `Monocle.App`; the test project already references App types used elsewhere (e.g. it tests `MainWindowViewModel`-adjacent services) — if it does not reference `Monocle.App`, add `<ProjectReference Include="..\..\src\Monocle.App\Monocle.App.csproj" />` to `tests/Monocle.Core.Tests/Monocle.Core.Tests.csproj` as part of this step.

```csharp
using System.IO;
using System.Text.Json;
using Monocle.App.Services;
using Xunit;

namespace Monocle.Core.Tests;

public class CullLauncherTests
{
    [Fact]
    public void McpServerExe_points_at_windowless_apphost_next_to_app()
    {
        var exe = CullLauncher.McpServerExe();
        Assert.EndsWith(Path.Combine("mcp", "Monocle.Mcp.exe"), exe);
    }

    [Fact]
    public void WriteMcpConfig_command_is_the_exe_with_no_dotnet_host()
    {
        var path = CullLauncher.WriteMcpConfig();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var monocle = doc.RootElement.GetProperty("mcpServers").GetProperty("monocle");
            var command = monocle.GetProperty("command").GetString();

            Assert.EndsWith("Monocle.Mcp.exe", command);
            Assert.DoesNotContain("dotnet", command!.ToLowerInvariant());
            // args must not smuggle a .dll back in via the dotnet muxer
            var args = monocle.GetProperty("args").EnumerateArray();
            foreach (var a in args)
                Assert.DoesNotContain(".dll", a.GetString()!);
        }
        finally { File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `& $dotnet test --filter "FullyQualifiedName~CullLauncherTests"`
Expected: FAIL — `McpServerExe` does not exist / `command` currently ends in `dotnet.exe`.

- [ ] **Step 3: Edit `CullLauncher.cs`**

Replace the DLL/host block (`src/Monocle.App/Services/CullLauncher.cs:12-27`) and the config body (lines 40-54). New content:

```csharp
    public static string McpServerExe() =>
        Path.Combine(AppContext.BaseDirectory, "mcp", "Monocle.Mcp.exe");

    public static bool McpServerExists() => File.Exists(McpServerExe());
```

Delete `McpServerDll()` and `DotnetHost()` entirely (nothing else should reference them after this — grep to confirm). In `WriteMcpConfig()`, replace the `server` dictionary and drop the `DOTNET_ROOT` env block:

```csharp
    public static string WriteMcpConfig()
    {
        var server = new Dictionary<string, object>
        {
            // Launch the windowless WinExe apphost directly: no dotnet muxer, so no console
            // window flashes when claude.exe spawns the MCP server as a grandchild.
            ["command"] = McpServerExe(),
            ["args"] = Array.Empty<string>(),
        };

        var config = new Dictionary<string, object>
        {
            ["mcpServers"] = new Dictionary<string, object> { ["monocle"] = server },
        };

        var path = Path.Combine(Path.GetTempPath(), $"monocle-cull-mcp-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }
```

- [ ] **Step 4: Fix references**

Grep for the removed members and update call sites:

Run: `grep -rn "McpServerDll\|DotnetHost" src/`
Expected after edit: only `CullLauncher.cs` history is gone; `MainWindowViewModel.cs:841` calls `CullLauncher.McpServerExists()` (unchanged name, still compiles). If any other file referenced `McpServerDll`/`DotnetHost`, repoint to `McpServerExe()`.

- [ ] **Step 5: Run tests to verify they pass**

Run: `& $dotnet test --filter "FullyQualifiedName~CullLauncherTests"`
Expected: PASS (both facts).

- [ ] **Step 6: Commit**

```bash
git add src/Monocle.App/Services/CullLauncher.cs tests/Monocle.Core.Tests/CullLauncherTests.cs tests/Monocle.Core.Tests/Monocle.Core.Tests.csproj
git commit -m "Launch MCP server via windowless apphost; no console flash on cull"
```

---

## Task 2: Make the exe the launch path; demote run-monocle.cmd to a dev build helper (A2)

**Files:**
- Modify: `run-monocle.cmd`
- Modify: `README.md`, `CLAUDE.md` (launch instructions only)

**Why:** `run-monocle.cmd` opens a console by nature. `Monocle.App.exe` is already `WinExe` (windowless). The self-contained publish (`scripts/publish-windows.ps1` → `publish/win-x64/Monocle.App.exe`) needs no `DOTNET_ROOT`, so double-clicking it is the headless launch path. Keep the `.cmd` only for dev iteration on the framework-dependent build.

This task is docs/comment only — no test.

- [ ] **Step 1: Re-caption `run-monocle.cmd` as a dev helper**

Edit the header comment of `run-monocle.cmd` (line 2):

```bat
rem DEV BUILD HELPER (not the end-user launch path). Builds if needed, sets DOTNET_ROOT for the
rem framework-dependent build, then runs the exe for quick iteration. For a headless, no-console
rem launch, use the self-contained publish/win-x64/Monocle.App.exe (scripts/publish-windows.ps1).
```

Leave the body as-is (it already runs the WinExe, so even via the .cmd the app window is windowless; the console is just the .cmd's own).

- [ ] **Step 2: Update README + CLAUDE.md launch wording**

In `README.md` and `CLAUDE.md`, where the launch path is described, make the documented **launch** path the exe:

> Launch: run `publish/win-x64/Monocle.App.exe` (self-contained, no runtime needed — build it with `pwsh scripts/publish-windows.ps1`). `run-monocle.cmd` is a dev build helper only.

Keep the existing `dotnet run --project src/Monocle.App` dev instructions.

- [ ] **Step 3: Sanity-build the publish path**

Run: `pwsh scripts/publish-windows.ps1`
Expected: produces `publish/win-x64/Monocle.App.exe`. Double-clicking it opens Monocle with no console window. (Manual check.)

- [ ] **Step 4: Commit**

```bash
git add run-monocle.cmd README.md CLAUDE.md
git commit -m "Document exe as the headless launch path; run-monocle.cmd is a dev helper"
```

---

## Task 3: Resizable Console / Run-log drawer (B)

**Files:**
- Modify: `src/Monocle.App/Views/MainWindow.axaml:176-203`

**Why:** The drawer is a fixed `Height="176"` `Border` docked bottom. Put a `GridSplitter` on its top edge so the user can drag it. No VM change, no persistence. UI-only → manual verification.

- [ ] **Step 1: Wrap the drawer in a splittable grid**

The drawer is docked `Bottom` inside the outer `DockPanel`. Replace the single bottom `Border` (`MainWindow.axaml:177-203`) with a bottom-docked `Grid` that holds a `GridSplitter` above the existing drawer border. Keep the whole thing gated by `IsVisible="{Binding ShowConsole}"` so it still hides when off.

```xml
        <!-- ===== CONSOLE / LOG PANEL (toggled in Settings) — resizable via the top splitter ===== -->
        <Grid DockPanel.Dock="Bottom" IsVisible="{Binding ShowConsole}" RowDefinitions="4,Auto">
            <GridSplitter Grid.Row="0" Height="4" HorizontalAlignment="Stretch"
                          Background="{StaticResource BorderSoft}" ResizeDirection="Rows"
                          Cursor="SizeNorthSouth"/>
            <Border Grid.Row="1" Height="176" MinHeight="90"
                    Background="{StaticResource Surface1}" BorderBrush="{StaticResource BorderSoft}"
                    BorderThickness="0,1,0,0">
                <!-- unchanged inner DockPanel: tab row + Console/Run-log TextBoxes (lines 180-202) -->
            </Border>
        </Grid>
```

Keep the inner `<DockPanel>…</DockPanel>` (current lines 180-202) verbatim inside the `Grid.Row="1"` border. The `GridSplitter` in a docked panel drags the drawer's `Height` (Avalonia adjusts the adjacent border's height); `MinHeight="90"` stops it collapsing to nothing.

> Note: because the drawer is `DockPanel.Dock="Bottom"`, dragging the splitter resizes the drawer border directly. If drag feel is wrong, the fallback is to move the whole center `Grid` (`MainWindow.axaml:206`) + drawer into a parent `Grid RowDefinitions="*,Auto,Auto"` (body / splitter / drawer) and drag the row. Prefer the docked-splitter form first — it is the smaller diff.

- [ ] **Step 2: Build and verify manually**

Run: `$env:DOTNET_ROOT="$env:LOCALAPPDATA\Microsoft\dotnet"; & $dotnet run --project src/Monocle.App`
Expected: with the console shown (Settings toggle), the bar above the drawer drags; both Console and Run-log tabs resize; it won't shrink below ~90px.

- [ ] **Step 3: Commit**

```bash
git add src/Monocle.App/Views/MainWindow.axaml
git commit -m "Make the console/run-log drawer resizable with a GridSplitter"
```

---

## Task 4: Claude models as pseudo-runners in the model registry (C + E2)

**Files:**
- Create: `src/Monocle.Models/Claude/ClaudeCullRunner.cs`
- Modify: `src/Monocle.App/ViewModels/MainWindowViewModel.cs:100-110` (register), `:427-428` (drop the standalone `ClaudeModels`/`ClaudeModel` after Process lands — see Task 7)
- Test: `tests/Monocle.Core.Tests/ClaudeTests.cs` (extend)

**Why:** To make Claude verdicts appear in the same checklist and per-model `scores` cache as other scorers, model the three Claude models as `IModelRunner`s. They are never scored per-photo (Claude culls the folder), so `ScoreAsync` throws and the Process command routes them to the cull path instead. `IsAvailableAsync` reports honestly (claude.exe + MCP server present).

**Interfaces:**
- Produces:
  - `ClaudeCullRunner(string modelId, string displayName)` with `Descriptor.Id == $"claude:{modelId}"`, `Descriptor.DisplayName == displayName`, `Descriptor.Category == ModelCategory.MllmCritique`, `Descriptor.Resource == ResourceKind.ClaudeTokens`, `Descriptor.OutputKind == ScoreKind.Aesthetic`.
  - `ClaudeCullRunner.ClaudeModelId` → `string` (the bare `modelId`, e.g. `claude-opus-4-8`, for the `--model` arg).
  - `static ClaudeCullRunner.Catalog` → `IReadOnlyList<ClaudeCullRunner>` (Haiku/Sonnet/Opus, in that order).
  - `static bool ClaudeCullRunner.IsClaudeId(string modelId)` → true when `modelId` starts with `"claude:"`.
- Consumes: `CullLauncher.McpServerExists()` (Task 1), `ModelCategory`, `ResourceKind`, `ScoreKind`, `ModelDescriptor` from `Monocle.Core.Model`.

> `Monocle.Models` must be able to see `CullLauncher.McpServerExists()`. `CullLauncher` currently lives in `Monocle.App`. To keep the dependency direction legal (App → Models, never Models → App), `IsAvailableAsync` must **not** reference `CullLauncher`. Instead check availability with logic local to Models: claude executable resolvable on PATH/`~/.local/bin` and the co-located MCP exe present. Add a tiny resolver in this file (mirror of `CullLauncher.ResolveClaude`/`McpServerExe`) rather than referencing App. Keep it dependency-free.

- [ ] **Step 1: Write the failing test (extend `ClaudeTests.cs`)**

```csharp
[Fact]
public void ClaudeCatalog_has_three_models_with_claude_prefixed_ids()
{
    var ids = Monocle.Models.Claude.ClaudeCullRunner.Catalog.Select(r => r.Descriptor.Id).ToList();
    Assert.Equal(3, ids.Count);
    Assert.All(ids, id => Assert.StartsWith("claude:", id));
    Assert.Contains("claude:claude-opus-4-8", ids);
    Assert.All(Monocle.Models.Claude.ClaudeCullRunner.Catalog,
        r => Assert.Equal(Monocle.Core.Model.ResourceKind.ClaudeTokens, r.Descriptor.Resource));
}

[Fact]
public async Task ClaudeRunner_ScoreAsync_throws_because_it_culls_the_folder_not_a_frame()
{
    var runner = Monocle.Models.Claude.ClaudeCullRunner.Catalog[0];
    await Assert.ThrowsAsync<NotSupportedException>(
        () => runner.ScoreAsync(null!).AsTask());
}

[Fact]
public void IsClaudeId_recognises_the_prefix()
{
    Assert.True(Monocle.Models.Claude.ClaudeCullRunner.IsClaudeId("claude:claude-haiku-4-5"));
    Assert.False(Monocle.Models.Claude.ClaudeCullRunner.IsClaudeId("qwen2-vl"));
}
```

(Confirm `ScoreAsync` returns `ValueTask<ModelScore>` in `IModelRunner`; if it returns `Task<ModelScore>`, drop `.AsTask()`.)

- [ ] **Step 2: Run test to verify it fails**

Run: `& $dotnet test --filter "FullyQualifiedName~ClaudeTests"`
Expected: FAIL — `ClaudeCullRunner` does not exist.

- [ ] **Step 3: Create `ClaudeCullRunner.cs`**

```csharp
using Monocle.Core.Model;

namespace Monocle.Models.Claude;

/// <summary>
/// Surfaces a single Claude model in the model checklist (#1 seam) so its cull verdict is stored and
/// shown per-model like any other scorer. Claude judges the whole folder via the CLI, not one frame,
/// so <see cref="ScoreAsync"/> is never called — the Process command routes ticked Claude models to
/// the folder cull path. Availability is honest: the CLI and the co-located MCP server must be present.
/// </summary>
public sealed class ClaudeCullRunner : IModelRunner
{
    public string ClaudeModelId { get; }

    public ClaudeCullRunner(string modelId, string displayName)
    {
        ClaudeModelId = modelId;
        Descriptor = new ModelDescriptor
        {
            Id = $"claude:{modelId}",
            DisplayName = displayName,
            Category = ModelCategory.MllmCritique,
            Description = "Culls the shoot with your own Claude Code — no API keys, locked to Monocle photo tools.",
            Tradeoffs = "Rich natural-language verdict; costs Claude tokens; runs the whole folder per click.",
            Resource = ResourceKind.ClaudeTokens,
            OutputKind = ScoreKind.Aesthetic,
        };
    }

    public ModelDescriptor Descriptor { get; }

    public static bool IsClaudeId(string modelId) =>
        modelId.StartsWith("claude:", StringComparison.Ordinal);

    // Ids verbatim from existing UI (MainWindowViewModel.cs:427) — do not invent new ones.
    public static readonly IReadOnlyList<ClaudeCullRunner> Catalog = new[]
    {
        new ClaudeCullRunner("claude-haiku-4-5", "Claude Haiku 4.5"),
        new ClaudeCullRunner("claude-sonnet-4-6", "Claude Sonnet 4.6"),
        new ClaudeCullRunner("claude-opus-4-8", "Claude Opus 4.8"),
    };

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        // Dependency-free availability (Models must not depend on App): claude resolvable + MCP exe present.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localClaude = Path.Combine(home, ".local", "bin",
            OperatingSystem.IsWindows() ? "claude.exe" : "claude");
        var mcpExe = Path.Combine(AppContext.BaseDirectory, "mcp", "Monocle.Mcp.exe");
        var claudeOk = File.Exists(localClaude) || ExistsOnPath(OperatingSystem.IsWindows() ? "claude.exe" : "claude");
        return Task.FromResult(claudeOk && File.Exists(mcpExe));
    }

    private static bool ExistsOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator)
        .Any(dir => !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir.Trim(), exe)));

    public ValueTask<ModelScore> ScoreAsync(ScoringContext context, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Claude culls the whole folder via the CLI; the Process command runs it, not per-frame ScoreAsync.");
}
```

Match `IModelRunner`'s exact `ScoreAsync` return type and `Descriptor` shape to the interface in `Monocle.Models` (adjust `ValueTask`/`Task`, and any required-init descriptor members, to compile against the real definitions).

- [ ] **Step 4: Register the three runners**

In `MainWindowViewModel.BuildRegistry` (`MainWindowViewModel.cs:100-110`), after the sidecar loop:

```csharp
        foreach (var claude in Monocle.Models.Claude.ClaudeCullRunner.Catalog)
            registry.Register(claude);
        return registry;
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `& $dotnet test --filter "FullyQualifiedName~ClaudeTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Monocle.Models/Claude/ClaudeCullRunner.cs src/Monocle.App/ViewModels/MainWindowViewModel.cs tests/Monocle.Core.Tests/ClaudeTests.cs
git commit -m "Add Claude models as pseudo-runners so verdicts key per-model in the registry"
```

---

## Task 5: Store each Claude cull verdict as a per-model ModelScore (C)

**Files:**
- Modify: `src/Monocle.App/ViewModels/MainWindowViewModel.cs` (cull tool-stream handler, currently `CullWithClaudeAsync` `set_rating` branch, `:908-914`)
- Add a small pure helper for parsing the tool input.
- Test: `tests/Monocle.Core.Tests/ClaudeTests.cs` (extend) + reuse `ShootCache` round-trip.

**Why:** The MCP `set_rating` mutates a single shared slot (`item.Stars`/`Rationale["headline"]`) in the MCP process. To keep each model's verdict, the app — which knows which Claude model is running — reads `(stars, rationale, model)` from the tool-use event and attaches a `ModelScore` keyed `claude:<modelId>` to the tile's item, then caches it. `item.Stars` (the XMP star) stays last-writer-wins, satisfying "the model that ran last sets the star". Verdicts coexist because they're keyed per model (the `scores` table PK is `(id, modelId)` — `ShootCache.cs:46-52`).

**Interfaces:**
- Produces: `static ModelScore MainWindowViewModel.ClaudeVerdictScore(string modelId, string displayName, int stars, string? rationale)` → a `ModelScore { ModelId = $"claude:{modelId}", ModelDisplayName = displayName, Kind = ScoreKind.Aesthetic, Value = stars, Text = rationale }`.
- Consumes: `TryGetToolId(ev.ToolInput)` (exists, `MainWindowViewModel.cs:899`); `_cache.PutScore` and `item.Scores` (from `ShootService`/`ShootCache`).

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void ClaudeVerdictScore_keys_by_model_so_models_do_not_collide()
{
    var haiku = MainWindowViewModel.ClaudeVerdictScore("claude-haiku-4-5", "Claude Haiku 4.5", 3, "slightly soft");
    var opus  = MainWindowViewModel.ClaudeVerdictScore("claude-opus-4-8", "Claude Opus 4.8", 4, "keeper");

    Assert.Equal("claude:claude-haiku-4-5", haiku.ModelId);
    Assert.Equal("claude:claude-opus-4-8", opus.ModelId);
    Assert.NotEqual(haiku.ModelId, opus.ModelId);   // distinct scores rows → no clobber
    Assert.Equal(4, opus.Value);
    Assert.Equal("keeper", opus.Text);
}
```

If `ClaudeVerdictScore` can't be `public static` on the VM without extra ceremony, put it as a `public static` method on `ClaudeCullRunner` instead and update the test's type — keep it a pure function so it's unit-testable.

- [ ] **Step 2: Run test to verify it fails**

Run: `& $dotnet test --filter "FullyQualifiedName~ClaudeTests"`
Expected: FAIL — `ClaudeVerdictScore` not defined.

- [ ] **Step 3: Add the pure helper**

Add to `MainWindowViewModel` (near the cull code):

```csharp
    /// <summary>Build the per-model verdict score for a Claude cull so Haiku/Sonnet/Opus verdicts
    /// coexist in the scores cache (keyed by model id) instead of overwriting one shared slot.</summary>
    public static ModelScore ClaudeVerdictScore(string modelId, string displayName, int stars, string? rationale) => new()
    {
        ModelId = $"claude:{modelId}",
        ModelDisplayName = displayName,
        Kind = ScoreKind.Aesthetic,
        Value = stars,
        Text = string.IsNullOrWhiteSpace(rationale) ? null : rationale!.Trim(),
        Resource = ResourceKind.ClaudeTokens,
    };
```

- [ ] **Step 4: Attach + cache the verdict in the `set_rating` branch**

In the cull tool-stream handler, extend the `set_rating` branch (`MainWindowViewModel.cs:908-914`) to pull stars/rationale/model from `ev.ToolInput` and store the per-model score on the matched tile. Add a `TryGetRating` helper mirroring `TryGetToolId` (reads `stars` int, `rationale` string; the running model id is known from the Process loop — pass it in via a captured local `currentClaudeModelId`/`currentClaudeDisplay`).

```csharp
                        if (tool.EndsWith("set_rating", StringComparison.Ordinal))
                        {
                            rated++;
                            Pipeline?.SetProgress("claude", Total == 0 ? 0 : Math.Min(1.0, (double)rated / Total));
                            if (tile is not null && TryGetStars(ev.ToolInput) is { } stars)
                            {
                                var verdict = ClaudeVerdictScore(currentClaudeModelId, currentClaudeDisplay,
                                                                 stars, TryGetRationale(ev.ToolInput));
                                tile.Item.Scores.RemoveAll(s => s.ModelId == verdict.ModelId);   // re-run replaces
                                tile.Item.Scores.Add(verdict);
                                _cache?.PutScore(tile.Item.Id, tile.Item.Fingerprint, verdict);  // match PutScore's real signature
                            }
                            tile?.CompleteCull();
                        }
```

Add the two tiny parse helpers next to `TryGetToolId`:

```csharp
    private static int? TryGetStars(JsonElement? input) =>
        input is { } el && el.TryGetProperty("stars", out var s) && s.TryGetInt32(out var v) ? v : null;

    private static string? TryGetRationale(JsonElement? input) =>
        input is { } el && el.TryGetProperty("rationale", out var r) ? r.GetString() : null;
```

(Match `TryGetToolId`'s actual parameter type — if it takes a `JsonNode`/`string`, mirror that. Confirm `PutScore`'s real signature at `ShootCache.cs:87` and pass the fingerprint the same way the scorer path does at `ShootService.cs:110-112`.)

- [ ] **Step 5: Run tests to verify they pass**

Run: `& $dotnet test --filter "FullyQualifiedName~ClaudeTests"`
Expected: PASS. Also run `& $dotnet test --filter "FullyQualifiedName~CacheAndServiceTests"` to confirm no cache regressions.

- [ ] **Step 6: Commit**

```bash
git add src/Monocle.App/ViewModels/MainWindowViewModel.cs tests/Monocle.Core.Tests/ClaudeTests.cs
git commit -m "Store each Claude cull verdict as a per-model score so models don't clobber"
```

---

## Task 6: Scan runs deterministic-only; no scorers (E3)

**Files:**
- Modify: `src/Monocle.App/ViewModels/MainWindowViewModel.cs` (`RunScanAsync` `:669-691`, `SetupPipeline` call)
- Test: `tests/Monocle.Core.Tests/ShootServiceTests.cs` (extend) — asserts the deterministic contract at the service seam.

**Why:** Scan currently runs `SelectedScorers()` (`:669`). Per the design, Scan must only decode/EXIF/metrics/heuristic-rate. The probabilistic scorers move to Process (Task 7). The service already supports this: `AnalyzeAsync(item, cache, rateIfUnrated: true, scorers: [], ct)` heuristic-rates and produces zero `ModelScore`s. Lock that contract with a test, then make Scan pass no scorers.

**Interfaces:**
- Consumes: `ShootService.AnalyzeAsync(PhotoItem, ShootCache, bool rateIfUnrated, IReadOnlyList<IModelRunner> scorers, CancellationToken)` (exists, `ShootService.cs:770` call site).

- [ ] **Step 1: Write the failing test (service-level, deterministic contract)**

Add to `ShootServiceTests.cs`, following the existing fixtures there (reuse the same sample-image + temp `ShootCache` setup the other tests in that file use — copy their arrange block rather than inventing one):

```csharp
[Fact]
public async Task AnalyzeAsync_with_no_scorers_rates_heuristically_and_produces_zero_model_scores()
{
    // arrange: (reuse this file's existing helper to load one PhotoItem + a temp ShootCache)
    var (service, item, cache) = LoadSingleSample();   // <- use the pattern already in ShootServiceTests

    await service.AnalyzeAsync(item, cache, rateIfUnrated: true,
                               Array.Empty<IModelRunner>(), CancellationToken.None);

    Assert.True(item.Stars >= 1);            // heuristic rated it (deterministic)
    Assert.Empty(item.Scores);               // no probabilistic model ran
}
```

If no `LoadSingleSample` helper exists, inline the same arrange steps the neighbouring tests use (they already construct a `ShootService` + `ShootCache` over a test asset).

- [ ] **Step 2: Run test to verify it fails or passes**

Run: `& $dotnet test --filter "DisplayName~no_scorers"`
Expected: PASS if the service already behaves; if it FAILS, that's a real bug to fix in `ShootService` before proceeding (the heuristic auto-rate must not depend on scorers being present). Either way this test now guards the Scan contract.

- [ ] **Step 3: Make Scan pass no scorers**

In `RunScanAsync` (`MainWindowViewModel.cs:669-691`), stop selecting scorers for a scan:

```csharp
            // Scan is deterministic-only now: decode → exif → metrics → heuristic rate. Probabilistic
            // model scoring moved to the Process button. Pass no scorers.
            IReadOnlyList<IModelRunner> scorers = Array.Empty<IModelRunner>();
            SetupPipeline(scorers);
            lock (_scorerSkipReasons) _scorerSkipReasons.Clear();

            var items = await Task.Run(() => _service.Load(folder, FoldPairs), ct);
            foreach (var item in items)
                Photos.Add(new PhotoTileViewModel(item) { ExpectsScoring = false });
            ApplyFilter();
            Pipeline?.SetStatus("scan", StageStatus.Done);
            // ...
            CullLog.Clear();
            RunLog($"Scan started — {Total} photos (deterministic: metrics + heuristic rating)");

            await AnalyzeAllAsync(scorers, ct);
```

Remove the now-dead `expectsScoring`/scorer-name logging in this method. `SetupPipeline([])` already skips the `aesthetic` stage (`:728-729`), so the flowchart correctly shows scoring as skipped for a scan.

- [ ] **Step 4: Build + manual check**

Run: `$env:DOTNET_ROOT="$env:LOCALAPPDATA\Microsoft\dotnet"; & $dotnet run --project src/Monocle.App -- "<a test folder>"`
Expected: Scan fills metrics + heuristic ratings; the Pipeline tab shows `aesthetic`/`claude` skipped; the Run log says "deterministic".

- [ ] **Step 5: Commit**

```bash
git add src/Monocle.App/ViewModels/MainWindowViewModel.cs tests/Monocle.Core.Tests/ShootServiceTests.cs
git commit -m "Scan is deterministic-only; scoring moves to Process"
```

---

## Task 7: The Process command — run ticked scorers + ticked Claude culls, ensure Qwen (E4 + C + D)

**Files:**
- Modify: `src/Monocle.App/ViewModels/MainWindowViewModel.cs` — new `ProcessCommand`; generalize the cull to a `RunClaudeCullAsync(ClaudeCullRunner)`; refactor `SelectedScorers()`; ensure the Qwen server; drop the standalone `ClaudeModels`/`ClaudeModel` combo state (`:427-428`).
- Modify: `src/Monocle.App/Views/MainWindow.axaml` — button rename + remove the ComboBox (done in Task 8's view edits; wiring here).

**Why:** One button does all probabilistic work, every click: run the ticked scoring runners per-photo (re-scoring on each click — probabilistic, no cache short-circuit), and run a folder cull for each ticked Claude model. Deterministic work is already cached from Scan. Qwen's server is started so it isn't silently skipped (D).

**Interfaces:**
- Consumes: `SelectedScorers()` (`:192`), `ClaudeCullRunner.IsClaudeId` + `.ClaudeModelId` (Task 4), `ClaudeVerdictScore` (Task 5), `LlamaServer.EnsureAsync` (`LlamaServer.cs:33`), `_sidecar` start (`StartSidecarAsync`).
- Produces: `ProcessCommand` (RelayCommand `ProcessAsync`), `RunClaudeCullAsync(ClaudeCullRunner runner, CancellationToken ct)`.

- [ ] **Step 1: Split scorers into per-photo runners vs Claude cull models**

Change `SelectedScorers()` (`:192-195`) to expose both, keeping the heuristic-only guard:

```csharp
    private IReadOnlyList<ModelOptionViewModel> EnabledModels() =>
        _heuristicOnly ? Array.Empty<ModelOptionViewModel>()
                       : Models.Where(m => m.IsEnabled && m.Available).ToList();

    // Per-photo scoring runners (everything except the folder-level Claude culls).
    private IReadOnlyList<IModelRunner> SelectedScorers() =>
        EnabledModels().Where(m => !ClaudeCullRunner.IsClaudeId(m.Runner.Descriptor.Id))
                       .Select(m => m.Runner).ToList();

    private IReadOnlyList<ClaudeCullRunner> SelectedClaudeModels() =>
        EnabledModels().Select(m => m.Runner).OfType<ClaudeCullRunner>().ToList();
```

- [ ] **Step 2: Add the Process command**

```csharp
    [RelayCommand]
    private async Task ProcessAsync()
    {
        if (_cache is null || string.IsNullOrEmpty(FolderPath) || Photos.Count == 0)
        {
            StatusText = "Scan a folder before processing.";
            return;
        }

        var scorers = SelectedScorers();
        var claude = SelectedClaudeModels();
        if (scorers.Count == 0 && claude.Count == 0)
        {
            StatusText = "Tick at least one model to process.";
            return;
        }

        // D: start Qwen's host so a ticked Qwen isn't silently skipped. EnsureAsync is a no-op when
        // GPU routing isn't configured; also (re)start the Python sidecar if a sidecar model is ticked.
        if (scorers.Any(r => r.Descriptor.RequiresSidecar))
        {
            await _llama.EnsureAsync();          // GPU route (MONOCLE_QWEN_LLAMA_URL); no-op otherwise
            if (!_sidecar.Running) await StartSidecarAsync();
        }

        // Probabilistic: run every ticked scorer for every frame, every click (re-scores on re-click).
        if (scorers.Count > 0)
        {
            IsBusy = true;
            try
            {
                SetupPipeline(scorers);
                lock (_scorerSkipReasons) _scorerSkipReasons.Clear();
                RunLog($"Process — scorers: {string.Join(", ", scorers.Select(s => s.Descriptor.DisplayName))}");
                await AnalyzeAllAsync(scorers, CancellationToken.None);
                CompletePipeline();
                RefreshStats();
                ApplyFilter();
            }
            finally { IsBusy = false; }
        }

        // Folder-level: one Claude cull per ticked Claude model, each storing a per-model verdict.
        foreach (var model in claude)
            await RunClaudeCullAsync(model, CancellationToken.None);

        StatusText = "Process complete.";
    }
```

Confirm `_llama` (a `LlamaServer`) is a field on the VM; if not, add one and dispose it with the VM. Confirm `_sidecar.Running` exists (used in `SidecarRunner.IsAvailableAsync`).

- [ ] **Step 3: Generalize the cull to a per-model method**

Rename `CullWithClaudeAsync` → `RunClaudeCullAsync(ClaudeCullRunner runner, CancellationToken ct)`. Replace the `ClaudeModel` string usages with `runner.ClaudeModelId` / `runner.Descriptor.DisplayName`, and set the captured locals the tool-stream handler needs (Task 5):

```csharp
    private async Task RunClaudeCullAsync(ClaudeCullRunner runner, CancellationToken outerCt)
    {
        if (!CullLauncher.McpServerExists())
        {
            CullLog.Add("Monocle MCP server not found next to the app — build the solution first.");
            return;
        }
        var currentClaudeModelId = runner.ClaudeModelId;
        var currentClaudeDisplay = runner.Descriptor.DisplayName;

        CullRunning = true;
        ShowConsole = true; DrawerRunLog = true;
        CullLog.Add($"Starting cull with {currentClaudeDisplay} (locked to Monocle photo tools)…");
        StatusText = $"Culling {Total} photos with {currentClaudeDisplay}…";
        // ... rest of the existing body unchanged, except:
        //   options.Model = runner.ClaudeModelId;   (was ClaudeModel)
        //   the set_rating branch stores ClaudeVerdictScore (Task 5), using the two captured locals.
    }
```

Keep the existing per-frame cull progress/creep logic verbatim. `_cullCts` still owns the cull's lifetime. (Running multiple Claude models sequentially reuses the same UI progress; that's fine.)

- [ ] **Step 4: Remove the standalone Claude combo state**

Delete `ClaudeModels`/`ClaudeModel` (`:427-428`) and the old `CullWithClaudeCommand` name (the button now binds `ProcessCommand` — Task 8). Grep to confirm no stragglers:

Run: `grep -rn "ClaudeModel\b\|ClaudeModels\|CullWithClaude" src/`
Expected: no references except historical comments; fix any.

- [ ] **Step 5: Build**

Run: `& $dotnet build Monocle.sln`
Expected: builds clean. (Behavioural verification is Task 8's manual pass, once the button/rail are wired.)

- [ ] **Step 6: Commit**

```bash
git add src/Monocle.App/ViewModels/MainWindowViewModel.cs
git commit -m "Add Process command: run ticked scorers + per-model Claude culls, ensure Qwen host"
```

---

## Task 8: Move AI Cull to the left rail; rename button to Process; remove the right tab (E1 + E4 + D)

**Files:**
- Modify: `src/Monocle.App/Views/MainWindow.axaml` — add rail **AI CULL** group between LIBRARY and CULL; add an AI Cull center view; remove the right-panel AI Cull tab; rename the button.
- Modify: `src/Monocle.App/ViewModels/MainWindowViewModel.cs` — `IsAiCull` view state + `GoViewCommand` param `AiCull`; drop `IsAiCullTab`/`SetRightTabCommand` AiCull wiring.

**Why:** Design decision — AI Cull becomes a left-rail nav entry that swaps the center view (like Library/Cull), holding the models checklist + Process button. Right panel keeps Detail + Pipeline. UI-heavy → manual verification.

- [ ] **Step 1: Add the view-state flag**

Follow the existing `IsBrowse`/`IsOverview`/`IsRejectsView`/`IsDesign` pattern in the VM (they're driven by `GoViewCommand` with a string param and exposed as bools). Add `IsAiCull` the same way, and make `GoViewCommand` accept `"AiCull"`. Ensure switching to AI Cull hides the browse grid etc. (mirror how `IsDesign` gates its panel).

- [ ] **Step 2: Add the rail nav group (between LIBRARY and CULL)**

In `MainWindow.axaml`, insert after the LIBRARY group (after line 266, before the `CULL` header at line 268):

```xml
                        <TextBlock Text="AI CULL" FontSize="10" FontWeight="SemiBold"
                                   Foreground="{StaticResource Text3}" Margin="8,14,0,4"/>
                        <Button Classes="nav" Classes.on="{Binding IsAiCull}" Command="{Binding GoViewCommand}"
                                CommandParameter="AiCull" Content="✦  Models &amp; Process"/>
```

- [ ] **Step 3: Add the AI Cull center view**

In the CENTER `Panel` (`MainWindow.axaml:283`, alongside the Browse grid and other views), add a view gated by `IsAiCull` that hosts the models checklist + Process controls moved out of the old right tab. Reuse the exact `ItemsControl ItemsSource="{Binding Models}"` block from the old AI Cull tab (`MainWindow.axaml:778-808`) so Claude models now render as checkboxes alongside the others. Replace the old CLAUDE CULL subsection (ComboBox + "Cull with Claude", `:810-817`) with a single Process button:

```xml
                <ScrollViewer IsVisible="{Binding IsAiCull}">
                    <StackPanel Spacing="12" Margin="18,14" MaxWidth="720" HorizontalAlignment="Left">
                        <TextBlock Text="MODELS" FontWeight="SemiBold" FontSize="11" Foreground="{StaticResource Text3}"/>
                        <TextBlock Text="Tick models, then Process. Scan first for metrics; Process runs the ticked models (and Claude, if ticked) every click."
                                   Foreground="{StaticResource Text3}" FontSize="11" TextWrapping="Wrap"/>
                        <Button Classes="ghost" Content="Start Python sidecar" Command="{Binding StartSidecarCommand}"
                                IsEnabled="{Binding !SidecarStarting}" HorizontalAlignment="Left" FontSize="11"/>
                        <!-- MODELS ItemsControl: copy verbatim from old lines 778-808 -->
                        <Rectangle Height="1" Fill="{StaticResource BorderSoft}" Margin="0,4"/>
                        <Button Classes="primary" Content="Process" HorizontalAlignment="Left" MinWidth="160"
                                Command="{Binding ProcessCommand}" IsEnabled="{Binding !CullRunning}"/>
                        <ProgressBar Minimum="0" Maximum="1" Value="{Binding ProgressFraction}" Height="6"/>
                    </StackPanel>
                </ScrollViewer>
```

- [ ] **Step 4: Remove the right-panel AI Cull tab**

In the right panel tab row (`MainWindow.axaml:675-682`), remove the middle `AI Cull` button and change the tab `Grid` to two columns `ColumnDefinitions="*,*"` (Detail | Pipeline). Delete the entire `<!-- AI CULL TAB -->` `ScrollViewer` block (`:767-819`). Remove `IsAiCullTab` and the `AiCull` case from `SetRightTabCommand` in the VM; make Detail the default right tab.

- [ ] **Step 5: Build + full manual verification**

Run: `$env:DOTNET_ROOT="$env:LOCALAPPDATA\Microsoft\dotnet"; & $dotnet run --project src/Monocle.App -- "<test folder>"`
Verify:
- Left rail shows LIBRARY → **AI CULL** → CULL in that order; clicking "Models & Process" swaps the center to the models list.
- The models list includes Claude Haiku/Sonnet/Opus checkboxes.
- **Scan** populates metrics + heuristic only (Pipeline shows scoring skipped).
- **Process** with a scoring model ticked re-scores every click; with a Claude model ticked it runs a cull; with both, both.
- Ticking Haiku then Opus and processing each leaves **both** verdicts in the detail pane's "AI CRITIQUE" cards; the star equals the last model run.
- With Qwen ticked and its server available, the Qwen critique shows on the selected photo; if the server can't start, the Run log records a skip reason (not silent).
- Right panel shows only Detail + Pipeline; no console window appears at launch or during a cull.

- [ ] **Step 6: Commit**

```bash
git add src/Monocle.App/Views/MainWindow.axaml src/Monocle.App/ViewModels/MainWindowViewModel.cs
git commit -m "Move AI Cull to left rail; rename button to Process; drop right AI Cull tab"
```

---

## Task 9: Full build + test sweep

- [ ] **Step 1: Build everything**

Run: `& $dotnet build Monocle.sln`
Expected: clean build, no warnings introduced by these changes.

- [ ] **Step 2: Run the whole test suite**

Run: `& $dotnet test`
Expected: all green, including the new `CullLauncherTests`, `ClaudeTests` additions, and the `ShootServiceTests` deterministic-scan test.

- [ ] **Step 3: Commit any final fixups**

```bash
git add -A
git commit -m "Finalize headless launch / per-model verdicts / Scan-Process reorg"
```

---

## Self-review — spec coverage

- **A1 cull flash** → Task 1. **A2 exe launcher** → Task 2.
- **B resizable drawer** → Task 3.
- **C per-model verdicts** → Tasks 4 (runners/keys) + 5 (verdict storage). Last-writer star = untouched `item.Stars`.
- **D Qwen visible** → Task 7 Step 2 (ensure host) + existing `BuildComments` renders `s.Text`; skip reasons already surface via `_scorerSkipReasons` (`MainWindowViewModel.cs:1259-1268`).
- **E1 left-rail nav** → Task 8. **E2 Claude in checklist** → Tasks 4 + 8 Step 3. **E3 Scan-only** → Task 6. **E4 Process button** → Tasks 7 + 8.

**Deferred (per spec):** drawer height persistence; user-designated primary model. Both intentionally out.

**Known verification caveats to confirm during implementation** (mismatches to reconcile against the real source, not guesses):
- `IModelRunner.ScoreAsync` return type (`Task` vs `ValueTask`) and `ModelDescriptor` required members — match exactly (Tasks 4, 5).
- `ShootCache.PutScore` signature + how fingerprint is passed (`ShootService.cs:110-112`) — mirror it (Task 5).
- `TryGetToolId`'s parameter type — mirror for `TryGetStars`/`TryGetRationale` (Task 5).
- Whether the test project already references `Monocle.App` (Task 1 Step 1).
