# Per-Device Concurrency + Per-Drive Segmented Progress — Design

> Fork feature for `alwyslep/videoduplicatefinder` (branch `add-korean-localization`).
> Two features that share **one foundation** (`group work by physical drive`):
> **(A)** concurrency that adapts to each drive's storage type, and
> **(B)** a status-bar progress bar split into one colored segment per drive.

## Motivation

`GatherInfos` (the scan/analysis phase that reads files to build grayBytes + audio
fingerprints) runs `Parallel.ForEachAsync(Database, MaxDegreeOfParallelism = Settings.MaxDegreeOfParallelism)`
— `ScanEngine.cs:696`. The live setting is `-1` (unbounded / all cores, `Settings.json`).

**Problem:** the optimal concurrency is *storage-type dependent* and a scan spans mixed drives:
- **SSD/NVMe** (D:, E:) — high concurrency wins (no seek penalty; needs queue depth to saturate).
- **Spindle HDD over USB** (H:, I:) — low concurrency wins; unbounded parallel makes the single
  head thrash between many files' scattered (interleaved-audio) reads. Measured on I:: ~25 MB/s,
  queue 24, 43 ms/read, 47 KB I/Os = seek-bound thrash (vs ~150 MB/s sequential write on the same drive).

A single global `MaxDegreeOfParallelism` cannot be right for a D:+E:+I: scan at once. **The fix is
to group scan work by drive and give each group its own concurrency.** That same grouping is exactly
what a per-drive progress bar needs — so both features are built on it.

## Shared foundation: group work by drive

At scan start, partition `Database` by `Path.GetPathRoot(entry.Path)` (drive letter as a proxy for
physical device — good enough; a true device map is a later refinement). For each drive group compute
`TotalBytes = Σ entry.FileSize` and `TotalFiles`. This one structure powers both A and B.

```
DriveGroup { string Root; List<FileEntry> Entries; long TotalBytes; int TotalFiles;
             long DoneBytes; int DoneFiles; int Concurrency; Brush Color; }
```

---

## Part A — Concurrency (per-device static → adaptive)

### Current state
- `GatherInfos` loop: `ScanEngine.cs:696`, DOP = `Settings.MaxDegreeOfParallelism` (raw, `-1`).
- Compare loops: `ScanEngine.cs:1263/1326/1348/1362` use the same raw setting.
- `ParallelDegree` (`:101`) maps `0 → -1`; used by partial-clip compare (`:1434`) and visual gate (`:1486`).
- **`Parallel.ForEachAsync` fixes DOP at call time — it cannot change mid-run.** This is the key
  constraint that shapes the adaptive design.

### Target — three rungs (build in order, stop where it's good enough)

1. **(#1) Measure the direction first — zero code.** Set `MaxDegreeOfParallelism = 2`, rescan an
   I:-heavy set, watch whether *completed files/sec* rises vs `-1`. If completion rate goes **up with
   fewer threads**, the thrash hypothesis is proven and the rest is justified. (Measurement caveat below.)

2. **(#2) Per-device static concurrency.** Replace the single whole-DB `Parallel.ForEachAsync` with
   **one `Parallel.ForEachAsync` per drive group**, each with that group's `Concurrency`:
   - SSD/NVMe → high (e.g. cores, or `-1`).
   - Spindle HDD → low (1–2).
   - Drive-type detection: `DriveInfo` doesn't expose SSD-vs-HDD directly. Options: (a) a small
     per-drive override map in `Settings` (user knows their fleet — see memory `usb-disk-fleet-health`);
     (b) heuristic probe (a quick random-read latency test at scan start: <2 ms ⇒ SSD, ≥5 ms ⇒ HDD);
     (c) both — probe with a settings override. **Recommend (a) first** (deterministic, user's fleet is known).
   - Drive groups may run **concurrently with each other** (I: at DOP 2 while D: at DOP 8) since they're
     independent physical devices — a fast drive isn't held back by a slow one.

3. **(#3) Adaptive controller (AIMD hill-climb).** Only if per-device static proves insufficient
   (unknown/mixed storage, drifting optimum). Because `Parallel.ForEachAsync` can't retune, replace it
   with a **custom concurrency limiter per drive group**:
   - A `SemaphoreSlim` whose permit count is adjusted at runtime (release more to grow; make returning
     workers not re-acquire to shrink), **or** a `Channel<FileEntry>` producer + a worker pool whose
     size is grown/retired live.
   - **Signal:** bytes-decoded/sec over a rolling window (not files/sec — file sizes vary too much).
   - **Algorithm (AIMD):** additive-increase concurrency by 1 every window while throughput improves;
     multiplicative-decrease (halve, or step down) when throughput drops. Damping/hysteresis to avoid
     oscillation. **Must decrease, not only increase** — on a spindle HDD "1 → up" overshoots into thrash,
     so backoff is mandatory. Per-drive controllers run independently.

### Measurement caveat (matters for #1 and #3)
Audio fingerprints **cache** (`ExtractAudioFingerprint` is skipped when `entry.AudioFingerprint != null`,
`ScanEngine.cs:814`). Once the current `-1` scan finishes, a rescan **skips audio decode entirely** —
so there's nothing to read and a concurrency A/B on cached files measures nothing. To measure the
concurrency→throughput relationship, test on an **uncached subset** (a drive/folder whose fingerprints
aren't built yet), or deliberately keep a small test folder uncached. grayBytes (visual) decode is also
concurrency-sensitive and can serve as the test signal if audio is fully cached.

---

## Part B — Per-drive segmented progress bar

### Current state
- Data: `ScanProgressChangedEventArgs` (`ScanProgressChangedEventArgs.cs`) is **global only** —
  `CurrentFile, CurrentPosition (n), MaxPosition (N), Elapsed, Remaining, CurrentStage, StageCurrent/Max`.
  No per-drive split, no bytes.
- GUI: `MainWindowVM.Scanner_Progress` (`:575`) → `ScanProgressValue/MaxValue/Count/Text`.
- XAML: single `PbScanProgress` `ProgressBar MinWidth="80" Height="12"` inside a horizontal StackPanel
  (`MainWindow.xaml:1292`) — **fixed width, does not span the window.**

### Target
A single horizontal bar that **spans the status-bar / window width** and is **divided into one segment
per drive**, where:
- each segment's **width ∝ that drive's `TotalBytes`** (a drive with more data to process is wider);
- each segment **fills independently** (`DoneBytes / TotalBytes`) with a **distinct per-drive color**;
- segments are separated by a **`|` divider** (1 px border);
- the whole bar's length is **dynamic to the VDF window width** (segments are proportional, so they
  reflow automatically).

```
|■■■■■■■□□□  D: 71% |■■□□□  E: 34% |■□□□□□□□□  I: 12%|
   (wide = D: has most bytes)         (narrow = less)   (colors differ per drive)
```

The per-drive color **is** the visual payoff of Part A: you watch each drive advance at its own
concurrency-driven pace (I: red crawling, D: green racing).

### Data model change
Extend the progress event with a per-drive snapshot (throttled like the existing event):
```
struct DriveProgress { string Root; long TotalBytes; long DoneBytes; int TotalFiles; int DoneFiles; }
// ScanProgressChangedEventArgs gains:  DriveProgress[] Drives;
```
Engine: build `DriveGroup`s at `InitProgress`; in `IncrementProgress(entry)` add `entry.FileSize` to that
drive's `DoneBytes` and `DoneFiles++`; include the per-drive array in the emitted event.

### GUI change
- New `ObservableCollection<DriveProgressVM>` on `MainWindowVM`; each item: `Root`, `WidthWeight`
  (= TotalBytes), `Fraction` (= DoneBytes/TotalBytes), `Brush` (palette by drive index).
- Replace `PbScanProgress` with a full-width container (own status row, `HorizontalAlignment=Stretch`):
  a `Grid` whose `ColumnDefinitions` are `*`-weighted by each drive's `TotalBytes` (proportional widths);
  each column hosts a mini fill (a `Border` background + a `Rectangle`/inner `ProgressBar` in the drive's
  `Brush`, `Fraction`-wide), with a 1 px separator `Border` (the `|`) between columns.
- Keep the textual `n/N`, elapsed, remaining, and churning-filename readouts as-is beside/below the bar.

### Note
The segmented bar can ship **before** any concurrency change — it visualizes per-drive progress even under
today's `-1` model, and it's the most visible quality-of-life win. It only needs the per-drive totals
(the shared foundation), not the scheduler rework.

---

## Build stages (agreed order)

0. **Design doc** — this file. ✅
1. **Shared foundation + data model** — `DriveGroup` partition at scan start; per-drive totals;
   `DriveProgress[]` on the progress event. (Enables both A and B.) ✅ (`7e96ddc`)
2. **Part B UI** — segmented per-drive progress bar (ships on current `-1`; highest visible value).
   ✅ (`7e96ddc`; deployed 2026-07-02). `WeightedStackPanel` + `DriveProgressVM` + `MainWindowVM.DriveSegments`.
3. **Part A #2** — per-device concurrency. ✅ (`9785617`; deployed 2026-07-02). Drive-grouped `GatherInfos`
   (`ProcessEntry` local fn + one `Parallel.ForEachAsync` per drive, `Task.WhenAll`). Drive type via a
   seek-latency probe (`ProbeSeekLatencyMs`, 3ms threshold) rather than a settings map. Fast drives share
   one CPU budget (configured DOP / `Environment.ProcessorCount`) split across them; HDD =
   `HddMaxDegreeOfParallelism` (default 2, serialized in Settings.json); DOP=1 stays strictly serial.
   Adversarial review (7 agents) caught + fixed the `-1 == ProcessorCount` oversubscription and the DOP=1
   cross-group race before commit.
4. **Part A #1** — DOP measurement on an uncached subset; confirm low-concurrency wins on I:. ← next
   (tune `HddMaxDegreeOfParallelism` in Settings.json to 1/2/4, rescan an uncached folder, compare I: rate).
5. **Part A #3** — adaptive AIMD controller (custom per-group limiter), only if #2 is insufficient.

### Related: Stop/Pause semantics (observed while building)
`Stop()` is the graceful shutdown: cooperative cancel → `GatherInfos` swallows the cancel → an
unconditional atomic `SaveDatabase()` flush → scan ends; fingerprints are cached so a later rescan
resumes (skips done). `Pause()` only freezes (no flush) and, under `-1` parallelism, has a long drain
tail (dozens of in-flight files must finish before parking) — another symptom that per-device low
concurrency (#2) fixes. A clean "pause = safe suspend + flush" is best folded into #2/#3, not bolted on.

## Open decisions
- Drive-type source: settings override map (recommended) vs latency probe vs both.
- Progress metric: bytes (recommended, size-fair) vs files. Bar fill uses bytes; counter can stay files.
- Color palette: fixed by drive index, or user-assignable per drive.
- Does "fit window width" mean a dedicated full-width status row (recommended) vs stretching within the
  current inline StackPanel? A dedicated row reads best and avoids fighting the WrapPanel stats layout.
