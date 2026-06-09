# Photo Critic — Feature Summary

A local-first tool for rating and culling photo shoots. It combines a browser-based
photo viewer with an AI culling job (Claude) and a no-AI heuristic fallback. Ratings
are written in a format On1 Photo RAW reads back.

---

## 1. Photo viewing & browsing

- **Folder scan** — paste a folder path, hit Scan, and get a thumbnail grid of every
  supported image in it. Last-used folder is remembered between sessions.
- **Thumbnail grid** — responsive grid of previews with lazy-loading. Each card shows
  the filename, current star rating, a technical-quality score, an aesthetic score,
  and a burst-group tag.
- **Detail drawer** — click any photo for a larger preview plus full metrics
  (technical score, sharpness, exposure, highlight/shadow clipping %, ISO, aesthetic
  score + which GPU/CPU backend produced it, burst group) and the AI rationale.
- **Keyboard-driven review** — arrow keys / `h`·`l` move between photos; `1`–`4` set
  stars, `0` clears, `P` picks (4★), `R`/`X` rejects (1★), `Esc` closes, `/` jumps to
  the folder box.
- **Filters** — one-click chips: All, ★2+, ★3+, ★4, Picks, Rejects, Unrated.
- **Visual cues** — green border = pick, red border = reject; a colored corner dot
  flags the technical reason a frame is weak.
- **Progress bar** — shows how many photos in the folder are rated vs. still to review.
- **Legend** — always-visible key explaining border colors and label-dot colors.

## 2. Rating & metadata

- **1–4 star scale** — 1 = reject, 2 = not good, 3 = average, 4 = good or better.
  Anything above 2★ is a "pick", 1★ is a "reject".
- **Manual rating** — set stars or pick/reject from the detail drawer (click or
  keyboard).
- **Pick/Reject keywords** — the matching `Pick` / `reject` keyword is added
  automatically based on the star rating (because On1's own flags don't travel in
  sidecars).
- **Color labels = the technical reason a frame is weak** (not pick/reject):
  Red = sharpness/focus, Blue = exposure, Purple = noise, Yellow = 2+ problems. Clean
  frames get no label.
- **Technical reason keywords** — e.g. `soft`, `underexposed`, `noisy` added on
  technical down-rates so they can be filtered later.
- **Per-criterion rationale** — a short remark on each of: sharpness, exposure, noise,
  other technical, artistic, storytelling, nostalgia, eye contact, happy moment.
- **One frame = two files** — a rating written to a RAW+JPG pair is mirrored onto both
  files automatically.
- **Records the judging model** — the model name (e.g. "Opus 4.8") is recorded with
  each rating.
- **Plain-text sidecar** — every rationale is also mirrored to a human-readable
  `<name>.txt` ("N★ — headline + one line per criterion").
- **Reads existing ratings** — picks up ratings from XMP sidecars, the proprietary
  `.on1` file, or XMP embedded in a DNG.
- **Safe writes** — only standard XMP sidecars are written; `.on1` files are never
  touched. Existing sidecars are backed up (`.xmp.bak` / `.txt.bak`) before the first
  edit, and existing fields not being changed are preserved.

## 3. AI culling (Claude)

- **Interactive cull (chat)** — a chat panel where Claude works through the folder in
  small batches, reports progress, and you can steer it in plain language ("only keep
  4★+", "go", "stop"). It states a plan and waits for a go-ahead before expensive work.
- **Unattended cull (one-shot)** — fire-and-forget job that rates the whole folder and
  streams a live log + progress bar, no interaction.
- **Live progress streaming** — both modes stream each step (tool calls, commentary,
  ratings landing) to the browser; ratings appear in the grid as they're written.
- **Run from the terminal** — a launcher script runs the same cull headlessly, with an
  option to pick the model (e.g. Opus for quality, Haiku for huge folders) and a debug
  log option.
- **Run as a slash command** — `/cull <folder>` inside Claude Code; it also offers to
  start the viewer so you can watch ratings land live.
- **Judges from the JPG / preview** — never demosaics a RAW just to look at it; judges
  the out-of-camera JPEG (or the RAW's embedded preview).
- **Burst de-duplication with series protection** — keeps the strongest frames in a
  burst and down-rates the rest, but always keeps at least 3 frames of a genuine series.
- **Cost/time reporting** — the run reports turns taken, duration, and dollar cost.
- **Locked-down execution** — the cull job can only use the project's own photo tools;
  it can't shell out, write arbitrary files, or use the user's other MCP servers.

## 4. Heuristic baseline (no AI)

- **Instant, offline rating** — a local algorithm rates every photo 1–4★ by combining
  the technical score with the aesthetic score, no API calls or tokens.
- **Automatic fault detection** — flags soft focus, bad exposure (clipping or off
  mean luma), and high-ISO noise, and assigns the matching color label/keyword.
- **Hard rejects** — unrecoverably soft frames or frames with 2+ faults are forced to
  1★.
- Useful as a fallback, for testing, or when you don't want to spend tokens.

## 5. Technical-quality metrics (automatic)

Computed on every photo, cheaply and deterministically:

- **Sharpness / focus** — best-tile and whole-frame focus measure (best-tile handles
  shallow depth of field, so a sharp subject on soft bokeh doesn't read as blurry).
- **Exposure** — penalizes highlight/shadow clipping and badly off-center exposure.
- **Highlight & shadow clipping** — fraction of blown / crushed pixels.
- **Contrast** and **mean brightness**.
- **ISO** — read from EXIF, used as a noise proxy.
- **Composite technical score** — a single 0–1 quality number (sharpness-weighted).
- **EXIF orientation honored** — portrait shots are analyzed upright, not sideways.

## 6. GPU aesthetic pre-filter (optional)

- **AI aesthetic score** — a CLIP + aesthetic-head model scores each photo's visual
  appeal (~1–10), so the AI cull can focus its attention on real candidates.
- **GPU-accelerated** — runs on the GPU via DirectML (works on AMD), with automatic
  fallback to CPU, and degrades gracefully (cull just judges everything) if the models
  aren't installed.
- **Near-duplicate / burst grouping** — groups bursts and bracketed frames using image
  embeddings (or a perceptual-hash fallback), gated by capture-time proximity so
  similar shots taken hours apart aren't merged.
- **One-shot precompute** — score + group an entire folder up front and cache it.

## 7. Reject management

- **Rejects stay put by default** — culling rates a reject 1★ in place and never moves
  or deletes anything; you sort later in On1.
- **Opt-in move-to-_Rejects** — explicitly move 1★ rejects into a `_Rejects` subfolder,
  carrying all their sidecars with them.
- **Dry-run by default** — the move reports its plan first; you must opt in again to
  actually move files.

## 8. Browsing/scanning surfaces (for tooling)

- **Folder overview** — counts, number of near-duplicate groups, largest group, star
  histogram, whether the aesthetic pre-filter ran, and a technical-quality breakdown.
- **Paginated photo listing** and **per-group drill-down** for large folders.
- **Command-line scanner** — list a folder's photos with metrics + ratings as a table
  or JSON.
- **Caching** — metrics, aesthetic scores, embeddings, and preview JPEGs are cached
  per shoot and auto-invalidated when a file changes.

## 9. Supported formats

- **RAW**: ARW, CR3, CR2, NEF, DNG, RAF, ORF, RW2.
- **Direct**: JPG/JPEG, PNG, TIF/TIFF, WEBP.
- **RAW+JPG pairs** treated as a single frame.
