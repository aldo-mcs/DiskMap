# DiskMap roadmap

Tracking what's left from the current feature plan. Core infrastructure for most of
this already exists (`AppSettings`, `ScanOptions`, `ScanCheckpoint`, `ScanErrorCollector`,
`FileCategory`/`Categorizer`, `SnapshotStore`/`SnapshotDiff`) — what's listed below is
specifically the remaining UI wiring and the genuinely-new pieces.

## Next up

- [ ] **Scan error visibility** — surface `ScanErrorCollector` results ("N files skipped")
      in the status bar with a small viewer window listing the skipped paths.
- [ ] **Resume-scan prompt** — `ScanCheckpoint` resume logic exists in `DirectoryScanner`
      but has no staleness check yet (a checkpoint's cached directory sizes are trusted
      without comparing against the directory's current `LastWriteTimeUtc`). Fix that
      first, then add a "Resume previous scan?" prompt when a checkpoint exists for the
      target path.
- [ ] **`app.manifest`** — explicit `requestedExecutionLevel level="asInvoker"` so the app
      can never drift into requiring admin by default.
- [ ] **Category rollup UI** — a stacked bar above the FILE TYPES list (data layer —
      `ExtensionStatsBuilder.BuildCategories()`, `FileTypeColors.GetCategoryBrush()` —
      already exists; this is UI wiring only). Toggle between by-category and
      by-extension views.

## Planned features (not started)

- [ ] **Space leak detection** — `SpaceLeakDetector` in Core comparing two snapshots
      (reusing `SnapshotStore`/`SnapshotDiff`), surfaced as a banner in the History
      window: "Folder increased by X% (+Y) since last scan."
- [ ] **Smart cleanup suggestions** — beyond the existing known-cache categories, scan the
      already-loaded tree for: old Downloads (>90 days), old/large archives, large stale
      files (untouched >1 year). Display + reveal + recycle only, same as the existing
      Cleanup tab — never auto-delete.
- [ ] **Largest Files dashboard** — a window with Top Files / Top Folders / Largest
      Growth / Largest Media tabs, backed by a new single-pass `TopItemsFinder`.
- [ ] **Portable incident report export** — self-contained HTML (print-to-PDF friendly)
      and JSON export of a scan's summary, category breakdown, space leaks, and
      duplicates. No new dependencies — `System.Text.Json` is already in use.

## Known follow-ups

- [ ] Visually confirm the theme-neutral file-type colors + subtle gradient (added in
      `FileTypeColors.cs`) look right across both themes on a real scan — the logic was
      written and unit-tested but not yet eyeballed live.
- [ ] Smooth animated zoom transition on treemap drill-in.
- [ ] Per-extension filtering by clicking a legend swatch.

## Guardrails (carried over from the existing plan, still apply)

- No new NuGet dependencies without a strong reason — local file I/O only, zero telemetry.
- `FollowSymlinks` as a separate setting was deliberately rejected — `ReparseBehavior`
  is the single source of truth for that.
- Cleanup-suggestion heuristics ship narrow first (3 confident categories) rather than
  all 5 originally proposed; revisit "duplicate ISOs" / "unused installers" only if
  there's real demand.
