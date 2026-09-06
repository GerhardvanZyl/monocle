# Graph Report - monocle  (2026-09-05)

## Corpus Check
- 209 files · ~331,857 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 2715 nodes · 5812 edges · 140 communities (120 shown, 17 thin omitted)
- Extraction: 90% EXTRACTED · 10% INFERRED · 0% AMBIGUOUS · INFERRED: 599 edges (avg confidence: 0.84)
- Token cost: 412,001 input · 35,828 output

## Community Hubs (Navigation)
- Main Window ViewModel
- Python Sidecar Client
- Design Canvas Runtime
- Image Decode Seam
- Technical Metrics Model
- Model Descriptors & Score Compositing
- Photo Query Filtering
- Path Guards & Shoot State
- Catalog & Folder Tree
- Catalog Entry ViewModel
- Catalog Freshness & Formats
- Shoot Cache
- Shoot Stats & Charts
- XMP Sidecar Tests
- Main Window Commands
- Models & Process Screen
- Project Files & Dependencies
- Sidecar Launch From UI
- Main Window Code-Behind
- Photo Item Model
- Pipeline Pips Control
- XMP Sidecar Writer
- Browse & Filmstrip Screens
- Shoot Service Tests
- Charts, Export & Pipeline Glue
- App Settings
- Main Window Rating Actions
- Photo Tile ViewModel
- Rating History Tests
- Heuristic & Aesthetic Scoring
- Rating Edit Records
- Outside-Edit Detection
- ONNX Image Preprocessing
- Rating History
- Sidecar Staleness Guard
- Flowchart Control
- Claude Cull Tests
- Pipeline Run
- Reject Moving
- Model Option ViewModel
- Aesthetic Calculator Tests
- Model Runner Seam
- ONNX Model Config
- Pipeline Row ViewModel
- Claude Stream Parsing
- Main Window Settings Binding
- Crop Window
- Photo Files & Atomic Writes
- Photo Critic Design Canvas
- AI Rating Snapshots
- Metadata Format Seam
- App Startup
- Robot Mascot Icons
- Child Process Job Control
- Python Sidecar Server
- CSV/JSON Export
- Metrics Calculation
- ONNX Score Runner
- Zoom Image Control
- Orientation Math
- Claude Cull Service
- Theme Manager
- Notes Format
- Logging
- Sidecar Service & Keywords
- Sidecar Server Tests
- EXIF Reading
- ONNX Export Script
- Shoot Service Orchestration
- Model Group ViewModel
- Cull Command & Features
- Model Score
- Metrics Tests
- Build & Verify Workflow Notes
- Tile Score Display
- Project Documentation
- Sidecar Installer
- Default Model Catalog
- Sidecar Convention Notes
- Sidecar Self-Check
- Sidecar Scoring Endpoint
- Cull Launcher
- Pipeline Graph
- Score Display Formatting
- Claude Cull Runner
- Sidecar Invariant Notes
- Agent Memory Conventions
- Value Converters
- Sidecar Locking
- Llama.cpp Server
- Grid Navigation
- Model Catalog Docs
- Aesthetic Runner
- Cull Settings
- EXIF Reader Seam
- ONNX Model Installer
- AI Critique Lines
- Sidecar Model Catalog
- Folder Scanning
- Headless Reorg Design Alignment
- Headless Processing Design
- ONNX Model Docs
- Tile Badges
- Settings Persistence
- Cache Service Tests
- Processing Queue
- Pipeline Stage Assembly
- Sidecar Contract Rationale
- Icon Design Explorations
- Headless Processing Plan
- Sidecar Health Endpoint
- ONNX Exporter
- Claude CLI Invocation
- Claude Event Types
- ONNX Installer Tests
- Run Lifetime Notes
- Model Feature Docs
- ONNX Session Factory
- Model Catalog Tests
- Score Normalization
- Shoot Cache Lifetime
- Cull Resume
- ONNX Model Catalog
- Sidecar Feature Rationale
- Feature Design Links
- View Locator
- Versioning
- MCP Config
- Photo Row Virtualization
- Unsupported Models
- Feature Notes
- MVVM Conventions
- Feature Details
- Linux Publish Script
- XAML Binding Note
- Build Workflow Note
- Python Packaging

## God Nodes (most connected - your core abstractions)
1. `MainWindowViewModel` - 355 edges
2. `PhotoItem` - 140 edges
3. `Monocle.Core.Model` - 72 edges
4. `ShootCache` - 66 edges
5. `PhotoTileViewModel` - 61 edges
6. `ModelScore` - 44 edges
7. `XmpData` - 39 edges
8. `TechnicalMetrics` - 35 edges
9. `ShootService` - 35 edges
10. `AppSettings` - 34 edges

## Surprising Connections (you probably didn't know these)
- `Safe Sidecar Writes` --semantically_similar_to--> `Sidecar / On1 Contract`  [INFERRED] [semantically similar]
  FEATURES.md → CLAUDE.md
- `400 Parsing vs 503 Execution Failure Split` --semantically_similar_to--> `A Single Runner Throwing Is Swallowed`  [INFERRED] [semantically similar]
  .claude/agent-memory/sidekick/python-sidecar-conventions.md → CLAUDE.md
- `Heuristic baseline (no AI) rating` --semantically_similar_to--> `Heuristic baseline scorer`  [INFERRED] [semantically similar]
  design/uploads/FEATURES.md → docs/models.md
- `GPU aesthetic pre-filter` --semantically_similar_to--> `LAION CLIP+MLP aesthetic predictor`  [INFERRED] [semantically similar]
  design/uploads/FEATURES.md → docs/models.md
- `ONNX Runtime execution-provider auto-selection` --semantically_similar_to--> `Compute-target torch wheel selection`  [INFERRED] [semantically similar]
  models/README.md → python/README.md

## Import Cycles
- None detected.

## Hyperedges (group relationships)
- **ShootCache Lifetime and Run Draining Flow** — _claude_agent_memory_sidekick_heavy_monocle_run_lifetime_shootcache_lifetime, _claude_agent_memory_sidekick_heavy_monocle_run_lifetime_drainable_run_handles, _claude_agent_memory_sidekick_heavy_monocle_run_lifetime_undrainable_claude_cull_leg, _claude_agent_memory_sidekick_heavy_monocle_run_lifetime_cache_snapshot_guard, _claude_agent_memory_sidekick_heavy_monocle_run_lifetime_disposed_cache_is_a_state, _claude_agent_memory_sidekick_heavy_monocle_run_lifetime_cleanup_must_not_wait [EXTRACTED 1.00]
- **Sidecar Write Safety System (On1 interop)** — claude_sidecar_on1_contract, features_safe_writes, _claude_agent_memory_sidekick_heavy_monocle_sidecar_invariants_zero_star_never_clears_rating, _claude_agent_memory_sidekick_heavy_monocle_sidecar_invariants_headline_merge_is_additive, _claude_agent_memory_sidekick_heavy_monocle_sidecar_invariants_keyword_leak, _claude_agent_memory_sidekick_heavy_monocle_staleness_guard_belief_baseline, _claude_agent_memory_sidekick_heavy_monocle_staleness_guard_sidecar_staleness_check [INFERRED 0.85]
- **Headless GUI Verification Toolkit** — _claude_skills_verify_skill_verify_skill, _claude_skills_verify_skill_printwindow_screenshot, _claude_skills_verify_skill_ui_thread_responsiveness_probe, _claude_agent_memory_sidekick_monocle_build_workflow_ui_automation_offscreen_testing, _claude_agent_memory_sidekick_monocle_build_workflow_printwindow_cannot_capture_flyouts, _claude_agent_memory_sidekick_heavy_monocle_runtime_verification_foreground_window_verification, _claude_agent_memory_sidekick_heavy_monocle_runtime_verification_debug_build_lock [INFERRED 0.85]
- **Models selectable in the Monocle model picker** — docs_models_heuristic_baseline, docs_models_nima, docs_models_aesthetic_predictor_v2_5, docs_models_laion_clip_mlp_aesthetic, docs_models_q_align_onealign, docs_models_qwen2_vl_critique, docs_models_claude_vision, docs_superpowers_plans_2026_07_05_headless_processing_reorg_claudecullrunner [EXTRACTED 0.95]
- **Deterministic Scan / probabilistic Process split** — docs_superpowers_specs_2026_07_05_headless_processing_reorg_design_scan_deterministic_only, docs_superpowers_specs_2026_07_05_headless_processing_reorg_design_process_button, docs_superpowers_plans_2026_07_05_headless_processing_reorg_processcommand, docs_superpowers_plans_2026_07_05_headless_processing_reorg_selectedscorers, docs_superpowers_plans_2026_07_05_headless_processing_reorg_claudeverdictscore, design_photo_critic_v2_dc_models_view [EXTRACTED 0.90]
- **Monocle mascot and icon exploration lineage** — design_monocle_icons_dc_icon_study, design_monocle_critic_variants_dc_the_critic, design_monocle_critic_refinements_dc_refinements, design_monocle_robot_gentleman_dc_robot_gentleman, design_monocle_50_mixes_dc_fifty_mixes [EXTRACTED 0.90]
- **Neon robot icon: one SVG source rasterised to PNG/JPG and vendored into the app** — assets_icons_robot_neon_neon_robot_app_icon_svg, assets_icons_robot_neon_neon_robot_app_icon_png, assets_icons_robot_neon_neon_robot_app_icon_jpg, design_exports_robot_neon_neon_robot_mascot_svg, design_exports_robot_neon_neon_robot_mascot_png, design_exports_robot_neon_neon_robot_mascot_jpg, src_monocle_app_assets_robot_neon_shipped_app_icon_png [INFERRED 0.95]
- **Three style treatments of one mascot geometry: flat, handlebar-on-charcoal, neon glow** — design_exports_robot_flat_flat_robot_mascot_svg, design_exports_robot_handlebar_handlebar_robot_mascot_svg, design_exports_robot_neon_neon_robot_mascot_svg, design_exports_robot_flat_monocle_robot_mascot [EXTRACTED 1.00]
- **Shared icon design system: squircle frame, teal palette, magnifier-eye motif** — design_exports_robot_neon_squircle_icon_frame, design_exports_robot_handlebar_teal_brand_palette, design_exports_robot_flat_monocle_magnifier_motif, design_exports_robot_flat_monocle_robot_mascot [INFERRED 0.85]
- **Shared Photo Critic Chrome Across All Screens** — design_uploads_pasted_1787447259343_0_browse_grid_screen, design_uploads_pasted_1787447268655_0_filmstrip_screen, design_uploads_pasted_1787447276372_0_folder_overview_screen, design_uploads_pasted_1787447316107_0_reject_management_screen, design_uploads_pasted_1787447259343_0_photo_critic_shell [EXTRACTED 1.00]
- **Cull Decision Flow: analyze, inspect, rate, dispose** — design_uploads_pasted_1787447287031_0_stage_sequence, design_uploads_pasted_1787447268655_0_metrics_readout, design_uploads_pasted_1787447268655_0_rating_controls, design_uploads_pasted_1787447316107_0_non_destructive_reject_policy [INFERRED 0.85]
- **Shoot Quality Reporting Surfaces (tiles, histogram, scatter, status bar)** — design_uploads_pasted_1787447276372_0_stat_tiles, design_uploads_pasted_1787447276372_0_star_histogram, design_uploads_pasted_1787447276372_0_technical_vs_aesthetic_scatter, design_uploads_pasted_1787447259343_0_review_progress_status_bar [INFERRED 0.85]
- **AI cull configuration flow: pick models, weight them, set thresholds, write the Claude prompt, choose scope, process** — design_uploads_pasted_1787447331986_0_model_picker_list, design_uploads_pasted_1787447342524_0_weighted_scoring_section, design_uploads_pasted_1787447342524_0_threshold_rules, design_uploads_pasted_1787447342524_0_editable_system_prompt, design_uploads_pasted_1787447350038_0_scorer_scope_toggle, design_uploads_pasted_1787447350038_0_process_button_progress [EXTRACTED 1.00]
- **Model availability transparency: every unrunnable model is still listed with the reason it cannot run, its resource kind, its model card, and the console line that logged it** — design_uploads_pasted_1787447331986_0_unavailable_model_reason_text, design_uploads_pasted_1787447331986_0_resource_kind_badge, design_uploads_pasted_1787449967648_0_not_runnable_here_section, design_uploads_pasted_1787449967648_0_model_card_links, design_uploads_pasted_1787447331986_0_console_run_log_dock [INFERRED 0.85]
- **Shared visual language for a frame's verdict across grid tile, legend, inspector, and status bar** — design_uploads_pasted_1787447357374_0_pick_reject_border, design_uploads_pasted_1787447331986_0_colour_legend_panel, design_uploads_pasted_1787449847623_0_tq_aes_meter_pair, design_uploads_pasted_1787447331986_0_detail_inspector_panel, design_uploads_pasted_1787447357374_0_review_status_bar [INFERRED 0.85]

## Communities (140 total, 17 thin omitted)

### Community 0 - "Main Window ViewModel"
Cohesion: 0.02
Nodes (104): Array, IBrush, ObservableCollection, MainWindowViewModel, Catalog, CatalogCountText, Favourites, FolderTree (+96 more)

### Community 1 - "Python Sidecar Client"
Cohesion: 0.05
Nodes (45): HashSet, SidecarCatalogResponse, IEnumerable, RejectMover, CancellationToken, Exception, HttpClient, IReadOnlyList (+37 more)

### Community 2 - "Design Canvas Runtime"
Cohesion: 0.07
Nodes (61): boot(), collectProps(), compileAttr(), compileTemplate(), createComponentFactory(), getDC(), Dispatcher(), createExternalModules() (+53 more)

### Community 3 - "Image Decode Seam"
Cohesion: 0.06
Nodes (31): Length, MtimeTicks, Offset, ReadOnlySpan, SKBitmap, SKCodec, SKEncodedOrigin, SKMatrix (+23 more)

### Community 4 - "Technical Metrics Model"
Cohesion: 0.06
Nodes (32): keyword, reason, remark, TechnicalMetrics, CompositeScore, Contrast, HighlightClip, Iso (+24 more)

### Community 5 - "Model Descriptors & Score Compositing"
Cohesion: 0.07
Nodes (36): Dto, ModelCategory, AestheticPredictor, CloudJudge, Heuristic, MllmCritique, NumericIqa, ModelDescriptor (+28 more)

### Community 6 - "Photo Query Filtering"
Cohesion: 0.08
Nodes (29): IComparable, TechnicalReason, Exposure, Multiple, Noise, None, Sharpness, IEnumerable (+21 more)

### Community 7 - "Path Guards & Shoot State"
Cohesion: 0.10
Nodes (19): ArgumentException, ImageContentBlock, McpServerTool, Result, PathGuard, Description, JsonSerializerOptions, Task (+11 more)

### Community 8 - "Catalog & Folder Tree"
Cohesion: 0.07
Nodes (17): DriveInfo, List, Thickness, FolderNodeViewModel, ChevronAngle, Children, Depth, Indent (+9 more)

### Community 9 - "Catalog Entry ViewModel"
Cohesion: 0.04
Nodes (36): ObservableObject, DateTime, IBrush, CatalogEntryViewModel, FramesText, IsStale, Name, Path (+28 more)

### Community 10 - "Catalog Freshness & Formats"
Cohesion: 0.07
Nodes (20): IReadOnlyList, FolderScanner, FileRole, Jpg, Other, Raw, PhotoVariant, Jpg (+12 more)

### Community 11 - "Shoot Cache"
Cohesion: 0.11
Nodes (15): IDisposable, SqliteCommand, SqliteConnection, Dictionary, IReadOnlyDictionary, JsonSerializerOptions, List, Redoable (+7 more)

### Community 12 - "Shoot Stats & Charts"
Cohesion: 0.08
Nodes (31): Control, Color, Dictionary, DrawingContext, FormattedText, IBrush, IPen, Rect (+23 more)

### Community 13 - "XMP Sidecar Tests"
Cohesion: 0.19
Nodes (10): List, XmpData, Crop, Keywords, Label, Orientation, Rating, WritesRatingFields (+2 more)

### Community 14 - "Main Window Commands"
Cohesion: 0.09
Nodes (7): CancellationToken, Dictionary, IReadOnlyList, List, ObservableCollection, CancellationTokenSource, Task

### Community 15 - "Models & Process Screen"
Cohesion: 0.07
Nodes (39): AI Critique 'What Works / What Doesn't' Cards, Pick/Reject and Problem-Colour Legend Panel, Docked Console / Run Log Panel, Right Detail Inspector Panel (preview, rating, metrics, critique), Folder + Scan Toolbar with Rating Filter Chips, Left Navigation Sidebar (Library / AI Cull / Cull sections), Model Picker List with Per-Model Availability, Models & Process Screen (Monocle 0.1.35) (+31 more)

### Community 16 - "Project Files & Dependencies"
Cohesion: 0.07
Nodes (30): Avalonia (11.3.17), Avalonia.Desktop (11.3.17), Avalonia.Diagnostics (11.3.17), Avalonia.Fonts.Inter (11.3.17), Avalonia.Themes.Fluent (11.3.17), CommunityToolkit.Mvvm (8.4.1), MetadataExtractor (2.8.1), Microsoft.AI.DirectML (1.15.2) (+22 more)

### Community 17 - "Sidecar Launch From UI"
Cohesion: 0.09
Nodes (8): Progress, SidecarLauncher, Bitmap, CancellationTokenSource, IDisposable, SemaphoreSlim, Task, PipGateRelease

### Community 18 - "Main Window Code-Behind"
Cohesion: 0.08
Nodes (14): EventArgs, ListBox, NotifyCollectionChangedEventArgs, SizeChangedEventArgs, KeyEventArgs, PointerPressedEventArgs, PropertyChangedEventArgs, RoutedEventArgs (+6 more)

### Community 19 - "Photo Item Model"
Cohesion: 0.06
Nodes (35): DateTime, Dictionary, IReadOnlyList, List, PhotoItem, ActiveFile, ActiveVariant, BaseName (+27 more)

### Community 20 - "Pipeline Pips Control"
Cohesion: 0.08
Nodes (22): gap, heights, ICustomHitTest, pip, Color, DrawingContext, IBrush, IReadOnlyList (+14 more)

### Community 21 - "XMP Sidecar Writer"
Cohesion: 0.15
Nodes (8): IEnumerable, List, XmpSidecar, XmlDocument, XmlElement, XmlNamespaceManager, XmlNode, XmlNodeList

### Community 22 - "Browse & Filmstrip Screens"
Cohesion: 0.08
Nodes (31): The Critic - Refinements Gallery (duplicate capture), The Critic - Refinements Logo Gallery, Dark/Light Preview Toggle, Geometric Icon Variant Family (Diamond, Bauhaus, Hexagon), Monocle App Icon Mark (magnifier + smile), Browse Thumbnail Grid Screen, Console / Run Log Dock, Filter Bar (All / star buckets / Picks / Rejects / Unrated / TQ threshold) (+23 more)

### Community 23 - "Shoot Service Tests"
Cohesion: 0.23
Nodes (11): OperationCanceledException, ShootService, CancellationToken, Fact, Func, Task, FakeRunner, Descriptor (+3 more)

### Community 24 - "Charts, Export & Pipeline Glue"
Cohesion: 0.11
Nodes (8): Monocle.Pipeline, Monocle.Models.Stats, Monocle.Models.Scoring, Monocle.Models.Onnx, Monocle.App.ViewModels, Monocle.App.Controls, Monocle.Core.Model, Monocle.Models.Export

### Community 25 - "App Settings"
Cohesion: 0.07
Nodes (29): Dictionary, List, AppSettings, Accent, AestheticWeights, CardViz, Catalog, CullCriteria (+21 more)

### Community 27 - "Photo Tile ViewModel"
Cohesion: 0.10
Nodes (14): Bitmap, IBrush, IReadOnlyList, PhotoTileViewModel, ActivePipProgress, HasStatus, HasTechnical, IsPair (+6 more)

### Community 28 - "Rating History Tests"
Cohesion: 0.25
Nodes (4): Cache, History, Fact, RatingHistoryTests

### Community 29 - "Heuristic & Aesthetic Scoring"
Cohesion: 0.15
Nodes (7): Monocle.Models, Monocle.Models.Aesthetic, Monocle.Models.Heuristic, Monocle.Core.Cache, Monocle.Core.Imaging, Monocle.Core.Tests, Monocle.Core.Sidecars

### Community 30 - "Rating Edit Records"
Cohesion: 0.09
Nodes (22): FileName, DateTime, Dictionary, RatingEdit, After, AfterDisk, Batch, Before (+14 more)

### Community 31 - "Outside-Edit Detection"
Cohesion: 0.27
Nodes (3): IReadOnlyDictionary, Fact, SidecarOutsideEditTests

### Community 32 - "ONNX Image Preprocessing"
Cohesion: 0.12
Nodes (15): DenseTensor, RgbImage, Height, Rgb, Width, b, g, r (+7 more)

### Community 33 - "Rating History"
Cohesion: 0.13
Nodes (13): Func, IEnumerable, IReadOnlyList, List, Redoable, Undoable, RatingApplyResult, Changed (+5 more)

### Community 34 - "Sidecar Staleness Guard"
Cohesion: 0.11
Nodes (9): Dictionary, IReadOnlyDictionary, SidecarRatingState, Headline, Rating, Dictionary, Dictionary, IReadOnlyDictionary (+1 more)

### Community 35 - "Flowchart Control"
Cohesion: 0.19
Nodes (12): Color, Dictionary, DrawingContext, FormattedText, IBrush, IPen, Rect, Size (+4 more)

### Community 37 - "Pipeline Run"
Cohesion: 0.26
Nodes (6): Dictionary, PipelineRun, Graph, OverallProgress, Fact, PipelineTests

### Community 38 - "Reject Moving"
Cohesion: 0.10
Nodes (10): IReadOnlyList, CenterView, AiCull, Browse, Design, Filmstrip, Overview, Pipeline (+2 more)

### Community 39 - "Model Option ViewModel"
Cohesion: 0.09
Nodes (21): CancellationTokenSource, RelayCommand, ModelOptionViewModel, CanExportOnnx, CanInstall, Description, HasInfoUrl, HasOnnxDownload (+13 more)

### Community 40 - "Aesthetic Calculator Tests"
Cohesion: 0.16
Nodes (11): contrast, mean, AestheticCalculator, b, Fact, Func, g, gray (+3 more)

### Community 41 - "Model Runner Seam"
Cohesion: 0.13
Nodes (15): CancellationToken, Task, IModelRunner, Descriptor, ScoringContext, Gray, Item, PreviewJpeg (+7 more)

### Community 42 - "ONNX Model Config"
Cohesion: 0.09
Nodes (21): Func, ModelCategory, OnnxModelConfig, Category, Description, DisplayName, DownloadUrl, FileName (+13 more)

### Community 43 - "Pipeline Row ViewModel"
Cohesion: 0.10
Nodes (21): FontWeight, IBrush, Thickness, PipelineRowViewModel, Indent, IsSub, ModelId, Name (+13 more)

### Community 44 - "Claude Stream Parsing"
Cohesion: 0.20
Nodes (12): JsonElement, ClaudeEvent, CostUsd, DurationMs, IsError, Kind, NumTurns, Text (+4 more)

### Community 46 - "Crop Window"
Cohesion: 0.17
Nodes (9): Action, Bitmap, KeyEventArgs, Point, PointerEventArgs, PointerPressedEventArgs, Rect, RoutedEventArgs (+1 more)

### Community 47 - "Photo Files & Atomic Writes"
Cohesion: 0.12
Nodes (10): DateTime, PhotoFile, Extension, Fingerprint, ModifiedUtc, Path, Role, SizeBytes (+2 more)

### Community 48 - "Photo Critic Design Canvas"
Cohesion: 0.14
Nodes (19): Browse grid with comfortable/compact density, Photo Critic v1 design canvas, Cull modes: heuristic baseline, interactive, unattended, Folder overview with technical breakdown, LIBRARY / CULL / LEGEND left rail, Always-visible border/label-dot legend, Dry-run reject move with explicit confirmation, Browse thumbnail grid with TQ/AES badges (+11 more)

### Community 49 - "AI Rating Snapshots"
Cohesion: 0.17
Nodes (9): List, RatingSnapshot, Headline, Keywords, RatedByModel, Reason, Stars, StarText (+1 more)

### Community 50 - "Metadata Format Seam"
Cohesion: 0.16
Nodes (9): List, IMetadataFormat, Name, MetadataFormats, XmpMetadataFormat, Name, SidecarSaveKind, NonRatingEdit (+1 more)

### Community 51 - "App Startup"
Cohesion: 0.12
Nodes (8): AppBuilder, Application, Monocle.App, Monocle.App.Diagnostics, Monocle.App.Views, App, Program, UrlLauncher

### Community 52 - "Robot Mascot Icons"
Cohesion: 0.24
Nodes (18): Neon Glow SVG Filter (dual feGaussianBlur + feMerge), Neon Robot App Icon (JPG, assets/icons), Neon Robot App Icon (PNG, assets/icons), Neon Robot App Icon (SVG source, assets/icons), Flat Robot Mascot (JPG export), Flat Robot Mascot (PNG export), Flat Robot Mascot (SVG source), Monocle / Magnifier Eye Motif (circle + handle stroke) (+10 more)

### Community 53 - "Child Process Job Control"
Cohesion: 0.20
Nodes (12): Monocle.Core.Processes, DllImport, IntPtr, IO_COUNTERS, JOBOBJECT_BASIC_LIMIT_INFORMATION, MarshalAs, Process, ChildProcessJob (+4 more)

### Community 54 - "Python Sidecar Server"
Cohesion: 0.16
Nodes (16): catalog(), _gpu_usable_for_pyiqa(), _pyiqa_candidates(), _pyiqa_entries(), _pyiqa_metric(), Monocle Python sidecar — optional, app-managed. Exposes the full HuggingFace…, Catalogue rows for the pyiqa metrics. `device_of(id)` gives the torch device…, Whether the GPU can actually run these metrics, as opposed to merely being… (+8 more)

### Community 55 - "CSV/JSON Export"
Cohesion: 0.23
Nodes (6): Csv, Json, IEnumerable, ShootExporter, Fact, ExportTests

### Community 56 - "Metrics Calculation"
Cohesion: 0.15
Nodes (9): high, low, GrayImage, Height, Luma, Width, contrast, mean (+1 more)

### Community 57 - "ONNX Score Runner"
Cohesion: 0.17
Nodes (12): InvalidOperationException, CancellationToken, InferenceSession, IProgress, ModelDescriptor, ScoringContext, Task, OnnxScoreRunner (+4 more)

### Community 58 - "Zoom Image Control"
Cohesion: 0.15
Nodes (10): PointerReleasedEventArgs, PointerWheelEventArgs, Bitmap, Point, PointerEventArgs, PointerPressedEventArgs, StyledProperty, ZoomImage (+2 more)

### Community 59 - "Orientation Math"
Cohesion: 0.18
Nodes (5): OrientationMath, Fact, InlineData, Theory, RotationTests

### Community 60 - "Claude Cull Service"
Cohesion: 0.15
Nodes (13): Action, CancellationToken, List, Process, Task, ClaudeCullOptions, Folder, MaxTurns (+5 more)

### Community 61 - "Theme Manager"
Cohesion: 0.19
Nodes (7): accent, fg, Color, Dictionary, IEnumerable, ThemeManager, AccentKeys

### Community 62 - "Notes Format"
Cohesion: 0.18
Nodes (6): AiHeadline, IEnumerable, List, HeadlineEntry, NotesFormat, UserNotes

### Community 63 - "Logging"
Cohesion: 0.16
Nodes (7): ConsoleColor, Queue, Exception, IReadOnlyList, Log, FilePath, STAThread

### Community 64 - "Sidecar Service & Keywords"
Cohesion: 0.17
Nodes (6): File, IReadOnlySet, MonocleKeywords, List, SidecarService, Xmp

### Community 65 - "Sidecar Server Tests"
Cohesion: 0.20
Nodes (15): Stdlib self-check for the sidecar's model-readiness and device-fallback logic…, A score normalises against the scale the picker advertised, so the two must not…, Answer both hardware probes without touching hardware. These tests are about…, The bug this exists for: a metric that has been scoring happily on the GPU hits…, R3's wire plumbing: /health's "broken" field must reflect _pyiqa_broken, over a…, stub_probes(), test_a_metric_known_to_be_cpu_does_not_retry_the_gpu(), test_a_metric_that_failed_everywhere_drops_out_of_ready() (+7 more)

### Community 66 - "EXIF Reading"
Cohesion: 0.20
Nodes (6): Directory, ExifIfd0Directory, ExifSubIfdDirectory, DateTime, ExifReader, IExifReader

### Community 67 - "ONNX Export Script"
Cohesion: 0.21
Nodes (12): Module, Path, _check(), _export(), export_aesthetic(), export_nima(), main(), Build the two "GPU / not installed" ONNX scorers Monocle can't ship prebuilt.… (+4 more)

### Community 68 - "Shoot Service Orchestration"
Cohesion: 0.22
Nodes (6): CancellationToken, Task, IReadOnlyList, CancellationToken, IReadOnlyList, Task

### Community 69 - "Model Group ViewModel"
Cohesion: 0.14
Nodes (12): ObservableCollection, RelayCommand, ModelGroupViewModel, AvailableCount, CanTickAll, CanUntickAll, CountText, Models (+4 more)

### Community 70 - "Cull Command & Features"
Cohesion: 0.15
Nodes (14): Per-View-Model Duplicated Brush Constants, Batched Visual Cull Process, Burst Series Protection (keep at least 3), /cull Slash Command, Never Demosaic a RAW to Judge It, Locked-Down Claude Cull (no API keys), AI Culling with Claude, Best-Tile Focus Measure (+6 more)

### Community 71 - "Model Score"
Cohesion: 0.14
Nodes (12): DateTime, ModelScore, Kind, ModelDisplayName, ModelId, Normalized, Resource, ScaleMax (+4 more)

### Community 73 - "Build & Verify Workflow Notes"
Cohesion: 0.15
Nodes (13): Verify GetForegroundWindow Before Sending Keys, Synthetic Mouse Wheel Does Not Scroll Avalonia, Duplicate Button Name Ambiguity (toolbar vs nav rail), InvokePattern Element-Picking Heuristic, PrintWindow Cannot Capture Avalonia Flyouts, Screenshot-Pixel to Screen-Coordinate Mapping, UI Automation for Off-Screen Window Testing, Monocle Diagnostics Log (+5 more)

### Community 75 - "Project Documentation"
Cohesion: 0.28
Nodes (13): Monocle (local-first photo culling app), Monocle.App, Monocle.Core, Monocle.Core.Tests, Monocle.Mcp, Monocle.Models, Monocle.Pipeline, PipelineGraph Drives Execution and UI (+5 more)

### Community 76 - "Sidecar Installer"
Cohesion: 0.22
Nodes (10): ComputeTarget, Action, CancellationToken, Task, ComputeTarget, Cpu, Default, DirectMl (+2 more)

### Community 77 - "Default Model Catalog"
Cohesion: 0.15
Nodes (4): Monocle.Models.Claude, Monocle.Models.Sidecar, Monocle.Mcp, DefaultModelCatalog

### Community 79 - "Sidecar Convention Notes"
Cohesion: 0.18
Nodes (12): Sidecar Wrapper Script Pre-Seeding, 400 Parsing vs 503 Execution Failure Split, Lazy-Import / Runnability Readiness Pattern, MCP scan_folder Never Drives Sidecar Scoring, Readiness Never Checks Local Weight Files, SidecarClient Retry by Status Code, Monocle MCP Tool Allowlist, A Single Runner Throwing Is Swallowed (+4 more)

### Community 80 - "Sidecar Self-Check"
Cohesion: 0.24
Nodes (6): ExitCode, ITestOutputHelper, Stderr, Stdout, Fact, SidecarSelfCheckTests

### Community 81 - "Sidecar Scoring Endpoint"
Cohesion: 0.20
Nodes (12): _deps_ready(), _gpu_ready(), _mage_ready(), _pyiqa_ready(), _qwen_ready(), True only when every heavy dependency a model needs is actually installed., True once a CUDA/ROCm GPU is visible to torch. Probed once and cached (shared…, True only when Qwen can actually run — not merely when its packages import. The… (+4 more)

### Community 82 - "Cull Launcher"
Cohesion: 0.23
Nodes (3): CullLauncher, Fact, CullLauncherTests

### Community 83 - "Pipeline Graph"
Cohesion: 0.18
Nodes (9): StageStatus, Done, Interrupted, Pending, Running, Skipped, StageState, Progress (+1 more)

### Community 84 - "Score Display Formatting"
Cohesion: 0.26
Nodes (5): ScoreDisplay, Fact, InlineData, Theory, ScoreDisplayTests

### Community 85 - "Claude Cull Runner"
Cohesion: 0.21
Nodes (8): CancellationToken, IReadOnlyList, ModelDescriptor, ScoringContext, Task, ClaudeCullRunner, ClaudeModelId, Descriptor

### Community 86 - "Sidecar Invariant Notes"
Cohesion: 0.25
Nodes (11): sidekick-heavy Memory Index, Run Lifetime & ShootCache (memory note), Runtime & Scratch-Harness Verification (memory note), Sidecar Write Invariants (memory note), Read-Back Is the Only Truth, 0 Stars Never Clears xmp:Rating, Sidecar Belief Baseline, Staleness Guard (memory note) (+3 more)

### Community 87 - "Agent Memory Conventions"
Cohesion: 0.18
Nodes (11): Debug-Build-Only Build Lock (MSB3027), BumpPatchVersion Dirties version.txt, sidekick Memory Index, Monocle.App MVVM/XAML Conventions (memory note), Small Enum Lives With Its Class, Partial-Class-Per-Feature View Model, Monocle Build/Test Workflow (memory note), Orphaned dotnet.exe Host Locks bin/ (+3 more)

### Community 88 - "Value Converters"
Cohesion: 0.22
Nodes (7): Monocle.App.Converters, CultureInfo, IValueConverter, NotSupportedException, EqualityConverter, Task, Type

### Community 89 - "Sidecar Locking"
Cohesion: 0.29
Nodes (6): Mutex, ConcurrentDictionary, IDisposable, MonitorReleaser, MutexReleaser, SidecarLock

### Community 90 - "Llama.cpp Server"
Cohesion: 0.24
Nodes (6): CancellationToken, HttpClient, Process, Task, LlamaServer, Url

### Community 91 - "Grid Navigation"
Cohesion: 0.40
Nodes (3): Fact, List, GridNavigationTests

### Community 92 - "Model Catalog Docs"
Cohesion: 0.24
Nodes (10): aesthetic-predictor-v2.5, Any combination of models may be ticked, In-app Build (Python) ONNX export, Monocle model catalog, NIMA (Neural Image Assessment), Q-Align / OneAlign MLLM scorer, python/export_onnx.py one-time export script, Native ONNX model weights folder (+2 more)

### Community 93 - "Aesthetic Runner"
Cohesion: 0.24
Nodes (7): IModelRunner, CancellationToken, ModelDescriptor, ScoringContext, Task, AestheticRunner, Descriptor

### Community 94 - "Cull Settings"
Cohesion: 0.27
Nodes (7): ThresholdRuleSetting, Axis, Below, MaxStars, IReadOnlyCollection, IReadOnlyList, Axis

### Community 95 - "EXIF Reader Seam"
Cohesion: 0.20
Nodes (9): DateTime, ExifInfo, Camera, CaptureTimeUtc, Iso, Lens, Orientation, PixelHeight (+1 more)

### Community 96 - "ONNX Model Installer"
Cohesion: 0.36
Nodes (6): CancellationToken, HttpClient, IProgress, Task, OnnxModelInstaller, Stream

### Community 97 - "AI Critique Lines"
Cohesion: 0.22
Nodes (4): Author, Body, CritiqueLine, IEnumerable

### Community 98 - "Sidecar Model Catalog"
Cohesion: 0.28
Nodes (5): BaseHTTPRequestHandler, Handler, The advertised scale for one id, without building the whole catalogue (which…, _scale_of(), score_image()

### Community 100 - "Headless Reorg Design Alignment"
Cohesion: 0.22
Nodes (9): AI cull instructions with generated system prompt, Not-runnable-here model shelf with reasons, Models & process view, Weighted scoring with redistributed weights, Workstream E: AI Cull moves to the left rail, Process button runs all probabilistic work, Scan does only deterministic work, ONNX Runtime execution-provider auto-selection (+1 more)

### Community 101 - "Headless Processing Design"
Cohesion: 0.22
Nodes (9): Console / Run log bottom drawer, Safe sidecar writes (.xmp only, backups, never .on1), Global constraints (net10, no UI test project, graceful degrade, sidecar contract), Headless processing reorg implementation plan, Launch MCP server via windowless WinExe apphost, Workstream A: no visible terminals, Workstream B: resizable Console / Run-log drawer, Self-contained exe as the launch path (+1 more)

### Community 102 - "ONNX Model Docs"
Cohesion: 0.25
Nodes (9): Adding a model: ONNX runner vs sidecar catalog entry, Qwen2-VL free-text critique, Honest availability: surface skipped models in the run log, Workstream D: Qwen results are visible, Stdlib-only server with lazy torch/transformers import, llama.cpp Vulkan GPU server route, Sidecar HTTP API (/health, /models, /score), Sidecar CATALOG / SCORERS registry (+1 more)

### Community 104 - "Settings Persistence"
Cohesion: 0.22
Nodes (8): DateTime, CatalogEntrySetting, Frames, LastProcessed, LastScanned, Name, Path, Picks

### Community 107 - "Pipeline Stage Assembly"
Cohesion: 0.28
Nodes (8): IReadOnlyList, PipelineGraph, Stages, PipelineStage, DependsOn, Id, Resource, Title

### Community 108 - "Sidecar Contract Rationale"
Cohesion: 0.25
Nodes (8): Additive dc:description Headline Merge, SidecarService.Save with Headline Overrides, Color Labels Encode the Technical Reason, Sidecar / On1 Contract, One Frame = Two Files (RAW+JPG pair), Safe Sidecar Writes, Supported Formats (RAW + direct), Sidecars & On1 Interop

### Community 109 - "Icon Design Explorations"
Cohesion: 0.32
Nodes (8): 50 Mixes — subject x treatment crossbreed grid, Treatment x subject matrix method, Critic refinements — Geometric, Gentleman, AI Critic, Artist, The Critic — 25 variants of concept #22, Brand motifs: monocle lens, camera aperture, cull check, Monocle icon study (25 app-mark takes), The Robot Gentleman — 25 mascot variations, Monocle design tokens (teal accent, label colors, themes)

### Community 110 - "Headless Processing Plan"
Cohesion: 0.29
Nodes (8): Fixed Claude model ids (haiku-4-5 / sonnet-4-6 / opus-4-8), ClaudeCullRunner pseudo-IModelRunner, Claude verdict stored as ModelScore keyed claude:<modelId>, App -> Models dependency direction constraint, ProcessCommand / RunClaudeCullAsync, SelectedScorers vs SelectedClaudeModels split, XMP star is last-writer-wins, Workstream C: per-model Claude verdicts coexist

### Community 111 - "Sidecar Health Endpoint"
Cohesion: 0.25
Nodes (8): _load_kwargs(), from_pretrained kwargs for a GPU load. CPU fallback is deliberately disabled:…, GPU path #2: route the critique through a co-located llama.cpp Vulkan server…, Lazy-load Qwen2-VL and return a short critique string (no numeric score).…, Lazy-load Mage-VL and return a short critique string (no numeric score). Always…, _score_mage(), _score_qwen(), _score_qwen_llama()

### Community 112 - "ONNX Exporter"
Cohesion: 0.43
Nodes (4): Action, CancellationToken, Task, OnnxExporter

### Community 113 - "Claude CLI Invocation"
Cohesion: 0.29
Nodes (6): ClaudeCullOutcome, CullOutcomeKind, Cancelled, Completed, Interrupted, MonocleTools

### Community 114 - "Claude Event Types"
Cohesion: 0.25
Nodes (7): ClaudeEventKind, AssistantText, Init, Result, ToolResult, ToolUse, Unknown

### Community 115 - "ONNX Installer Tests"
Cohesion: 0.46
Nodes (3): Fact, Task, OnnxModelInstallerTests

### Community 116 - "Run Lifetime Notes"
Cohesion: 0.29
Nodes (7): Cleanup Must Never Block the UI Thread, Disposed ShootCache Is a State, Not an Error, Scratchpad Console Harness, settings.json Snapshot and Restore, WM_CLOSE for Clean App Shutdown, Stop-Process Bypasses Cleanup and Orphans the Sidecar, UI-Thread Responsiveness Probe (SendMessageTimeout WM_NULL)

### Community 117 - "Model Feature Docs"
Cohesion: 0.33
Nodes (7): AI culling with Claude, Burst de-duplication with series protection, GPU aesthetic pre-filter, Judge from JPEG / embedded preview, never demosaic, Locked-down cull execution, Claude (vision) cloud judge, LAION CLIP+MLP aesthetic predictor

### Community 118 - "ONNX Session Factory"
Cohesion: 0.48
Nodes (3): SessionOptions, InferenceSession, OnnxSessionFactory

### Community 121 - "Shoot Cache Lifetime"
Cohesion: 0.40
Nodes (6): ReferenceEquals Cache Snapshot Guard, Drainable Run Handles (_scanRun / _analysisRun), ShootCache Per-Shoot Lifetime, StartAnalysisAsync Entry Point, Deliberately Undrainable Claude Cull Leg, ProcessAsync/ScanAsync Swallow Their Own Exceptions

### Community 122 - "Cull Resume"
Cohesion: 0.40
Nodes (3): IEnumerable, IReadOnlyList, CullResume

### Community 123 - "ONNX Model Catalog"
Cohesion: 0.47
Nodes (3): IReadOnlyList, OnnxModelCatalog, Configs

### Community 124 - "Sidecar Feature Rationale"
Cohesion: 0.40
Nodes (5): Stale Pick/reject Keyword Leak in MergeKeywords, Keyboard-Driven Review, Pick/Reject Keywords, 1-4 Star Rating Scale, Reject Management (_Rejects, dry-run)

### Community 125 - "Feature Design Links"
Cohesion: 0.40
Nodes (5): Pipeline stage flowchart view, Best-tile sharpness for shallow depth of field, Heuristic baseline (no AI) rating, Technical-quality metrics, Heuristic baseline scorer

### Community 126 - "View Locator"
Cohesion: 0.40
Nodes (3): IDataTemplate, Control, ViewLocator

### Community 127 - "Versioning"
Cohesion: 0.50
Nodes (4): Keyboard-driven review bindings, Photo viewing and browsing, Supported formats (RAW + direct, RAW+JPG pairs), Monocle version 0.1.115

### Community 128 - "MCP Config"
Cohesion: 0.50
Nodes (3): DOTNET_ROOT, C:\Users\vanzy\AppData\Local\Microsoft\dotnet\dotnet.exe, monocle

### Community 129 - "Photo Row Virtualization"
Cohesion: 0.50
Nodes (3): IReadOnlyList, PhotoRowViewModel, Tiles

### Community 130 - "Unsupported Models"
Cohesion: 0.83
Nodes (3): IReadOnlyList, BlockedModelGroup, UnsupportedModelCatalog

### Community 131 - "Feature Notes"
Cohesion: 0.67
Nodes (3): Detail Drawer, Folder Scan, Thumbnail Grid

## Ambiguous Edges - Review These
- `Monocle version 0.1.115` → `Photo viewing and browsing`  [AMBIGUOUS]
  version.txt · relation: conceptually_related_to
- `Neon Robot App Icon (SVG source, assets/icons)` → `Shipped Monocle.App Neon Robot Icon (PNG)`  [AMBIGUOUS]
  assets/icons/robot-neon.svg · relation: conceptually_related_to
- `Star Rating Histogram` → `Chart Caption Overlap Layout Defect`  [AMBIGUOUS]
  design/uploads/pasted-1787447276372-0.png · relation: conceptually_related_to

## Knowledge Gaps
- **572 isolated node(s):** `C:\Users\vanzy\AppData\Local\Microsoft\dotnet\dotnet.exe`, `DOTNET_ROOT`, `monocle-sidecar`, `publish-linux.sh script`, `Stats` (+567 more)
  These have ≤1 connection - possible missing edges or undocumented components. (Counts symbols only; 924 node(s) total have ≤1 connection when file, concept and rationale nodes are included.)
- **17 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **What is the exact relationship between `Monocle version 0.1.115` and `Photo viewing and browsing`?**
  _Edge tagged AMBIGUOUS (relation: conceptually_related_to) - confidence is low._
- **What is the exact relationship between `Neon Robot App Icon (SVG source, assets/icons)` and `Shipped Monocle.App Neon Robot Icon (PNG)`?**
  _Edge tagged AMBIGUOUS (relation: conceptually_related_to) - confidence is low._
- **What is the exact relationship between `Star Rating Histogram` and `Chart Caption Overlap Layout Defect`?**
  _Edge tagged AMBIGUOUS (relation: conceptually_related_to) - confidence is low._
- **Why does `MainWindowViewModel` connect `Main Window ViewModel` to `Python Sidecar Client`, `Photo Row Virtualization`, `Unsupported Models`, `Photo Query Filtering`, `Catalog & Folder Tree`, `Catalog Entry ViewModel`, `Shoot Cache`, `Shoot Stats & Charts`, `Main Window Commands`, `Sidecar Launch From UI`, `Main Window Code-Behind`, `Photo Item Model`, `Shoot Service Tests`, `Charts, Export & Pipeline Glue`, `App Settings`, `Main Window Rating Actions`, `Photo Tile ViewModel`, `Rating History`, `Claude Cull Tests`, `Pipeline Run`, `Reject Moving`, `Model Option ViewModel`, `Model Runner Seam`, `Pipeline Row ViewModel`, `Main Window Settings Binding`, `AI Rating Snapshots`, `App Startup`, `CSV/JSON Export`, `Theme Manager`, `Logging`, `Shoot Service Orchestration`, `Model Group ViewModel`, `Model Score`, `Tile Score Display`, `Main Window Navigation`, `Llama.cpp Server`, `Grid Navigation`, `Cull Settings`, `AI Critique Lines`, `Folder Scanning`, `Cache Service Tests`, `Processing Queue`?**
  _High betweenness centrality (0.335) - this node is a cross-community bridge._
- **Why does `PhotoItem` connect `Photo Item Model` to `Main Window ViewModel`, `Python Sidecar Client`, `Image Decode Seam`, `Technical Metrics Model`, `Model Descriptors & Score Compositing`, `Photo Query Filtering`, `Path Guards & Shoot State`, `Catalog Freshness & Formats`, `Shoot Stats & Charts`, `XMP Sidecar Tests`, `Main Window Commands`, `Shoot Service Tests`, `Charts, Export & Pipeline Glue`, `Photo Tile ViewModel`, `Rating History Tests`, `Rating Edit Records`, `Outside-Edit Detection`, `Rating History`, `Sidecar Staleness Guard`, `Claude Cull Tests`, `Reject Moving`, `Model Runner Seam`, `Photo Files & Atomic Writes`, `AI Rating Snapshots`, `Metadata Format Seam`, `CSV/JSON Export`, `Orientation Math`, `Sidecar Service & Keywords`, `EXIF Reading`, `Shoot Service Orchestration`, `Model Score`, `EXIF Reader Seam`, `AI Critique Lines`, `Cache Service Tests`, `Cull Resume`?**
  _High betweenness centrality (0.239) - this node is a cross-community bridge._
- **Why does `PhotoTileViewModel` connect `Photo Tile ViewModel` to `Main Window ViewModel`, `Photo Row Virtualization`, `Model Descriptors & Score Compositing`, `Reject Moving`, `Tile Badges`, `Cache Service Tests`, `Tile Score Display`, `Catalog Entry ViewModel`, `Main Window Commands`, `Sidecar Launch From UI`, `Main Window Code-Behind`, `Photo Item Model`, `Pipeline Pips Control`, `Charts, Export & Pipeline Glue`?**
  _High betweenness centrality (0.070) - this node is a cross-community bridge._
- **Are the 3 inferred relationships involving `PhotoItem` (e.g. with `.CropRoundTripsThroughSidecar()` and `.ResetCropRemovesItFromSidecar()`) actually correct?**
  _`PhotoItem` has 3 INFERRED edges - model-reasoned connections that need verification._