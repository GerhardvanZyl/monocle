---
description: Cull a photo folder using Monocle's locked-down photo tools
---

You are culling the photo shoot in: **$ARGUMENTS**

Use **only** the `monocle` MCP tools — `scan_folder`, `get_preview`, `get_metrics`,
`set_rating`, `set_notes`, `list_burst_groups`. Do not use any other tools, shell, or file
access.

Process:

1. `scan_folder("$ARGUMENTS")` to list every frame with its technical metrics.
2. State a short plan and wait for a "go" before expensive work (skip the wait if told to
   proceed unattended).
3. Work in small batches. For each frame (or burst group), call `get_preview` to judge the
   out-of-camera JPEG / embedded preview **visually**, and combine that with the technical
   metrics. Never demosaic a RAW.
4. `set_rating(id, stars, rationale, model)` — 1 = reject, 2 = weak, 3 = average, 4 = good or
   better (anything > 2 is a pick). For bursts, keep the strongest frame(s) and down-rate the
   rest, but always keep at least 3 frames of a genuine series.
5. When done, report the number of picks/rejects and the turns, duration and cost.

> The `monocle` MCP server is registered in `.mcp.json`. If its paths don't match your machine,
> regenerate it (the Monocle app does this automatically when you launch a cull from the UI).
