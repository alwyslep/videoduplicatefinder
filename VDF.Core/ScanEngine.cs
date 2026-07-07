// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
//     VideoDuplicateFinder is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU Affero General Public License for more details.
//     You should have received a copy of the GNU Affero General Public License
//     along with VideoDuplicateFinder.  If not, see <http://www.gnu.org/licenses/>.
// */
//

global using System;
global using System.Collections.Concurrent;
global using System.Collections.Generic;
global using System.IO;
global using System.Threading;
global using System.Threading.Tasks;
global using Size = System.Drawing.Size;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text.Json;
using VDF.Core.FFTools;
using VDF.Core.Utils;
using VDF.Core.ViewModels;

namespace VDF.Core {
	/// <summary>
	/// One stage of the full-scan pipeline, for driving the stages individually (in this
	/// execution order) via <see cref="ScanEngine.StartStage"/>. Running all four in order is
	/// equivalent to a full scan.
	/// </summary>
	public enum ScanStage {
		BuildFileList,
		GatherInfos,
		Compare,
		PartialCompare,
	}

	public sealed partial class ScanEngine {
		public HashSet<DuplicateItem> Duplicates { get; set; } = new HashSet<DuplicateItem>();
		public Settings Settings { get; set; } = new Settings();
		public event EventHandler<ScanProgressChangedEventArgs>? Progress;
		public event EventHandler? BuildingHashesDone;
		public event EventHandler? ScanDone;
		public event EventHandler? ScanAborted;
		public event EventHandler? ThumbnailsRetrieved;
		public event Action<int, int>? ThumbnailProgress;
		public event EventHandler? FilesEnumerated;
		public event EventHandler? DatabaseCleaned;

		/// <summary>Encoded placeholder image (PNG/JPEG bytes) shown when thumbnail extraction fails.</summary>
		public byte[]? NoThumbnailImage;

		PauseTokenSource pauseTokenSource = new();
		CancellationTokenSource cancelationTokenSource = new();
		// SAFE stop (first Stop press during the file-reading phase): stops dispatching NEW files while
		// every in-flight file runs to 100%, so its fingerprint lands in the cache and is never re-decoded
		// on the next scan. Pause and Stop thus interrupt identically at a file boundary — they differ only
		// in what follows (park for resume vs save-and-end). Hard cancellation (the token) stays for the
		// second press / non-gather phases, where there is no per-file rework to protect.
		volatile bool stopRequested;
		readonly List<float> positionList = new();

		bool _isScanning;
		// ponytail: main process yields CPU to foreground apps (Explorer/Dopus) while a scan
		// runs, restored the instant scanning ends — hooked on the setter so EVERY exit path
		// (done/abort/stop via CancelAllTasks) restores. BelowNormal only cedes under
		// contention, so an unattended scan still runs at full speed. Best-effort.
		bool isScanning {
			get => _isScanning;
			set {
				if (_isScanning == value) return;
				_isScanning = value;
				try {
					using var p = System.Diagnostics.Process.GetCurrentProcess();
					p.PriorityClass = value ? System.Diagnostics.ProcessPriorityClass.BelowNormal
											: System.Diagnostics.ProcessPriorityClass.Normal;
				}
				catch { /* priority is a nicety; never let it break a scan */ }
			}
		}
		int scanProgressMaxValue;
		readonly Stopwatch SearchTimer = new();
		public Stopwatch ElapsedTimer = new();
		int processedFiles;
		DateTime startTime = DateTime.Now;
		DateTime lastProgressUpdate = DateTime.MinValue;
		static readonly TimeSpan progressUpdateIntervall = TimeSpan.FromMilliseconds(300);
		const int maxExcludedLogsPerReason = 5;
		readonly ConcurrentDictionary<string, int> excludedReasonCounts = new();
		readonly ConcurrentDictionary<string, int> excludedReasonLoggedCounts = new();
		// Files whose stored pHash for the comparison position is null. Dedupes the
		// per-pair log spam from #754: one bad file otherwise produces a line per
		// candidate it's compared against (thousands of lines from a handful of files).
		readonly ConcurrentDictionary<string, byte> missingPHashFiles = new(
			CoreUtils.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
		DateTime lastCheckpointTime = DateTime.MinValue;
		DateTime lastDriveLog = DateTime.MinValue;
		static readonly TimeSpan progressLogInterval = TimeSpan.FromSeconds(8);
		readonly object checkpointLock = new();
		
		// ── Per-drive scan progress (segmented status bar) ──
		// Built once at GatherInfos start (file-reading phase); left null during compare phases so their
		// IncrementProgress calls don't touch it. DoneBytes/DoneFiles mutate via Interlocked (parallel loop).
		// One worker's in-progress file on a drive. Keyed by managed thread id in DriveCounter.Active so
		// concurrent workers on the same drive each get their own stable slot instead of stomping a single
		// last-writer-wins field — this is what lets the UI show N rows for N active workers.
		sealed class ActiveFileState { public string Path = ""; public string? Stage; public int StageCur; public int StageMax; }
		// DesiredEnabled = the user's latest checkbox state; Disabled = what the workers obey. They differ
		// only while a live relist runs (re-enable mid-stage): Disabled stays true so the workers keep
		// parking until the relist has landed its new files, then the relist applies DesiredEnabled.
		// LateArrivals feeds files collected after the stage snapshot to the drive's claim loop.
		sealed class DriveCounter { public long TotalBytes; public int TotalFiles; public long DoneBytes; public int DoneFiles; public double Rate; public int CapOverride; public volatile bool Disabled; public volatile bool DesiredEnabled = true; public int RelistPending; public int Analyzed; public int MissingFiles; public int Fingerprinted; public int FingerprintTarget; public readonly ConcurrentQueue<FileEntry> LateArrivals = new(); public readonly ConcurrentDictionary<int, ActiveFileState> Active = new(); }
		Dictionary<string, DriveCounter>? driveCounters;
		string[]? driveOrder;
		// Live per-drive parallelism override chosen from the status-bar dropdown (0 = auto). RunDriveAdaptive's
		// control loop reads it each ~3s tick and applies it directly (bypassing AIMD), so a change applies
		// within seconds in either direction. No-op if that drive isn't scanning.
		public void SetDriveCap(string root, int cap) {
			var dc = driveCounters;
			if (dc != null && dc.TryGetValue(root, out var c)) c.CapOverride = System.Math.Max(0, cap);
		}
		// Live per-drive pause from the status-bar checkbox: a disabled drive claims no new
		// files (in-flight ones run to completion); re-enabling resumes within a second.
		// BuildDriveCounters recreates the counters with Disabled=false each scan/stage; the
		// GUI persists the checkbox per drive root and re-pushes an unchecked box every
		// progress tick (same pattern as the caps), so the state survives restarts.
		// Re-enabling DURING an adaptive gather stage first live-relists the drive (picks up
		// files created since the last file-list build) and only then unparks the workers —
		// unparking first lets them exhaust their claims and exit before the new work lands.
		public void SetDriveEnabled(string root, bool enabled) {
			var dc = driveCounters;
			if (dc == null || !dc.TryGetValue(root, out var c)) return;
			// Under driveLifecycleLock so a toggle can't interleave with a finishing relist's
			// "Disabled = !DesiredEnabled" apply — unlocked, that lost the toggle (a re-check
			// whose CAS failed stayed parked forever; an uncheck could be overwritten and the
			// "paused" drive kept claiming files). Monitor is reentrant, so the gatherLiveRelist
			// call below (which takes the same lock) is safe.
			lock (driveLifecycleLock) {
				c.DesiredEnabled = enabled;
				if (!enabled) { c.Disabled = true; return; }
				var relist = gatherLiveRelist;   // non-null only while the adaptive gather stage runs
				if (c.Disabled && relist != null) {
					if (System.Threading.Interlocked.CompareExchange(ref c.RelistPending, 1, 0) == 0)
						relist(root, c);
					return;   // the relist task applies DesiredEnabled when it finishes
				}
				c.Disabled = false;
			}
		}
		bool DriveDisabled(string root) {
			var dc = driveCounters;
			return dc != null && dc.TryGetValue(root, out var c) && c.Disabled;
		}
		// Drive roots whose GatherInfos loop has not finished yet (adaptive AND static paths).
		// Parked (paused) drives use it to detect "everyone left is paused" and skip out, so the
		// stage ends when the checked drives finish instead of waiting forever on unchecked ones.
		ConcurrentDictionary<string, byte>? runningDriveRoots;
		// Serializes drive-task lifecycle against the live-relist path: task teardown (root removal +
		// late-queue drain check), relist enqueue-vs-respawn decisions, and the stage's accepting flag.
		readonly object driveLifecycleLock = new();
		// Both are set only while the adaptive gather stage runs (cleared in its finally).
		Action<string, DriveCounter>? gatherLiveRelist;
		Action<string, ConcurrentQueue<FileEntry>>? gatherLateRespawn;   // must be invoked under driveLifecycleLock
		bool AllRemainingDrivesDisabled() {
			var rr = runningDriveRoots;
			if (rr == null || rr.IsEmpty) return false;
			var dc = driveCounters;
			foreach (var r in rr.Keys) {
				if (dc == null || !dc.TryGetValue(r, out var c)) return false;
				// A pending relist counts as active: the stage must stay open until its collected
				// files are enqueued or respawned, or they would be orphaned in the database.
				if (!c.Disabled || System.Threading.Volatile.Read(ref c.RelistPending) != 0) return false;
			}
			return true;
		}
		static string DriveRootOf(string path) { try { return System.IO.Path.GetPathRoot(path) ?? "?"; } catch { return "?"; } }
		// Records the calling worker's in-progress file (+ optional sub-stage) under its own thread-id slot.
		void SetDriveCurrent(string path, string? stage = null, int stageCurrent = 0, int stageMax = 0) {
			var dc = driveCounters;
			if (dc == null || !dc.TryGetValue(DriveRootOf(path), out var c)) return;
			var slot = c.Active.GetOrAdd(Environment.CurrentManagedThreadId, static _ => new ActiveFileState());
			slot.Path = path; slot.Stage = stage; slot.StageCur = stageCurrent; slot.StageMax = stageMax;
		}
		// DB-state-only "nothing left to do" predicate, mirroring ProcessEntry's skip/cache logic.
		// Seeds the per-drive bars with already-complete work at scan start — the bar shows the
		// drive's CUMULATIVE completion state, not this scan's throughput (the LCN ordering killed
		// the old start-of-scan flythrough that used to fake this) — and marks those entries so
		// their instant pass-through isn't counted a second time. Keep in sync with ProcessEntry.
		// A sampling-failed entry is permanently done — skipped, no more retries — when retry is off,
		// or when it has burned through the retry budget (a corrupt/truncated file never heals, so
		// retrying it every scan is pure waste). maxAttempts <= 0 disables the cap (retry forever).
		internal static bool SamplingPermanentlyFailed(bool hasThumbnailError, bool retryEnabled, int failCount, int maxAttempts)
			=> hasThumbnailError && (!retryEnabled || (maxAttempts > 0 && failCount >= maxAttempts));

		// A video whose frame sampling stayed permanently failed after burning the whole retry budget —
		// a genuinely undecodable/corrupt file (the container-duration inflation is already auto-repaired
		// by the re-probe rescue, so a survivor here is real corruption). The auto-delete trigger. Images
		// and the cap-disabled case (maxSamplingAttempts <= 0) are never auto-deleted.
		internal static bool IsUnrecoverableVideo(FileEntry e, int maxSamplingAttempts)
			=> !e.IsImage && e.Flags.Has(EntryFlags.ThumbnailError) &&
			   maxSamplingAttempts > 0 && e.SamplingFailCount >= maxSamplingAttempts;

		// Records one failed sampling attempt (saturating) and logs once, when the entry crosses the
		// retry budget, so the permanent skip is visible rather than silent.
		void NoteSamplingFailure(FileEntry entry) {
			if (entry.SamplingFailCount < byte.MaxValue)
				entry.SamplingFailCount++;
			int cap = Settings.MaxSamplingRetryAttempts;
			if (cap > 0 && Settings.AlwaysRetryFailedSampling && entry.SamplingFailCount == cap)
				Logger.Instance.Info($"Frame sampling failed {cap}x for '{entry.Path}' — permanently skipping it in future scans (fix the file or delete its DB entry to retry).");
		}

		bool EntryIsAlreadyComplete(FileEntry e) {
			if (e.Flags.Has(EntryFlags.ThumbnailError))
				return SamplingPermanentlyFailed(true, Settings.AlwaysRetryFailedSampling, e.SamplingFailCount, Settings.MaxSamplingRetryAttempts);
			if (!e.IsImage && e.mediaInfo == null) return false;
			if (e.grayBytes == null || (e.IsImage && e.grayBytes.Count == 0)) return false;
			if (!e.IsImage)
				for (int i = 0; i < positionList.Count; i++)
					if (!e.grayBytes.ContainsKey(GetGrayBytesIndex(e, positionList[i]))) return false;
			if (Settings.EnablePartialClipDetection && !e.IsImage &&
				!e.Flags.Has(EntryFlags.NoAudioTrack) &&
				!e.Flags.Has(EntryFlags.AudioFingerprintError) &&
				!e.Flags.Has(EntryFlags.SilentAudioTrack) &&
				e.AudioFingerprint == null) return false;
			return true;
		}
		// In-scope entries already complete at scan start, pre-counted into the progress totals.
		int preseededFiles;
		void BuildDriveCounters() {
			int preseeded = 0;
			var groups = new Dictionary<string, DriveCounter>(StringComparer.OrdinalIgnoreCase);
			foreach (var e in DatabaseUtils.Database) {
				// Count only entries this scan will actually touch. Out-of-scope entries never
				// report progress (InvalidEntry → reportProgress=false), so counting them left
				// whole drives stuck below 100% — and a drive with nothing in scope shouldn't get
				// a bar (or a seek probe / LCN walk) at all.
				if (!Settings.ScanAgainstEntireDatabase && !IsInIncludeScope(e)) continue;
				var root = DriveRootOf(e.Path);
				if (!groups.TryGetValue(root, out var dc)) { dc = new DriveCounter(); groups[root] = dc; }
				dc.TotalBytes += e.FileSize; dc.TotalFiles++;
				if (EntryIsAlreadyComplete(e)) {
					dc.DoneBytes += e.FileSize;
					dc.DoneFiles++;
					preseeded++;
				}
				// Audio-fingerprint inventory: cumulative DB state (not this scan's progress), so the
				// user can see coverage grow across interrupted scans. RAW holdings by user request:
				// N counts every entry carrying a fingerprint (flags included), M = N plus the ones a
				// fingerprint can still be computed for — N==M exactly when nothing computable remains.
				if (Settings.EnablePartialClipDetection && !e.IsImage) {
					if (e.AudioFingerprint != null) {
						dc.Fingerprinted++;
						dc.FingerprintTarget++;
					}
					else if (!e.Flags.Has(EntryFlags.NoAudioTrack) &&
							 !e.Flags.Has(EntryFlags.AudioFingerprintError) &&
							 !e.Flags.Has(EntryFlags.SilentAudioTrack))
						dc.FingerprintTarget++;
				}
			}
			// Seed saved per-drive caps AND the pause checkboxes BEFORE any worker launches, so a
			// capped drive starts AT its cap and a paused drive never claims a file (the GUI's
			// live pushes only land after the first progress event — too late for scan start).
			foreach (var kv in groups) {
				if (Settings.DriveWorkerCaps.TryGetValue(kv.Key, out var cap) && cap > 0)
					kv.Value.CapOverride = cap;
				kv.Value.Disabled = Settings.DriveDisabledDrives.Contains(kv.Key);
				kv.Value.DesiredEnabled = !kv.Value.Disabled;
			}
			var keys = new List<string>(groups.Keys); keys.Sort(StringComparer.OrdinalIgnoreCase);
			driveCounters = groups; driveOrder = keys.ToArray();
			preseededFiles = preseeded;
			processedFiles = preseeded;   // global counter starts at the cumulative baseline too
		}

		/// <summary>
		/// Cumulative per-drive DB state for display BEFORE any stage runs, so the status-bar
		/// pause checkboxes and worker caps have a surface to configure ahead of starting a
		/// scan. Same aggregation as <see cref="BuildDriveCounters"/> but pure — no scan state
		/// is touched. Requires the database to be loaded; uses the CURRENT settings (include
		/// scope, partial-clip toggle, thumbnail count), so sync GUI settings first.
		/// </summary>
		public DriveProgress[] GetDrivePreview() {
			if (DatabaseUtils.Database.Count == 0) return Array.Empty<DriveProgress>();
			BuildPositionList(); // EntryIsAlreadyComplete evaluates cached frames against it
			var groups = new Dictionary<string, DriveProgress>(StringComparer.OrdinalIgnoreCase);
			foreach (var e in DatabaseUtils.Database) {
				if (!Settings.ScanAgainstEntireDatabase && !IsInIncludeScope(e)) continue;
				var root = DriveRootOf(e.Path);
				groups.TryGetValue(root, out var dp);
				dp.Root = root;
				dp.TotalBytes += e.FileSize;
				dp.TotalFiles++;
				if (EntryIsAlreadyComplete(e)) {
					dp.DoneBytes += e.FileSize;
					dp.DoneFiles++;
				}
				if (Settings.EnablePartialClipDetection && !e.IsImage) {
					if (e.AudioFingerprint != null) {
						dp.Fingerprinted++;
						dp.FingerprintTarget++;
					}
					else if (!e.Flags.Has(EntryFlags.NoAudioTrack) &&
							 !e.Flags.Has(EntryFlags.AudioFingerprintError) &&
							 !e.Flags.Has(EntryFlags.SilentAudioTrack))
						dp.FingerprintTarget++;
				}
				groups[root] = dp;
			}
			return groups.Values.OrderBy(d => d.Root, StringComparer.OrdinalIgnoreCase).ToArray();
		}
		DriveProgress[]? DriveSnapshot() {
			var dc = driveCounters; var order = driveOrder;
			if (dc == null || order == null) return null;
			var arr = new DriveProgress[order.Length];
			for (int i = 0; i < order.Length; i++) {
				var c = dc[order[i]];
				var active = new DriveActiveFile[c.Active.Count];
				int j = 0;
				foreach (var kv in c.Active) {
					if (j >= active.Length) break;   // dictionary can grow between Count and enumeration; guard the copy
					var s = kv.Value;
					active[j++] = new DriveActiveFile { File = s.Path, Stage = s.Stage, StageCurrent = s.StageCur, StageMax = s.StageMax };
				}
				if (j < active.Length) Array.Resize(ref active, j);
				// Concurrency shown to the user IS active.Length (the same data backing the "now processing"
				// rows below it), not the controller's internal target/ceiling bookkeeping — a lowered cap
				// only stops NEW workers from starting (already-in-flight ones finish safely, per spec), so
				// the two numbers can legitimately differ for a while during the drain; showing the live
				// count instead of the target keeps the label truthful throughout that transition instead of
				// silently claiming "1" while several rows are still visibly active underneath it.
				arr[i] = new DriveProgress { Root = order[i], TotalBytes = c.TotalBytes, DoneBytes = c.DoneBytes, TotalFiles = c.TotalFiles, DoneFiles = c.DoneFiles, FilesPerSec = c.Rate, Concurrency = active.Length, DecodeWorkers = FFTools.FFmpegNative.ParallelAudioFingerprinter.ActiveDecodeCount(order[i]), ActiveFiles = active, Analyzed = c.Analyzed, Missing = c.MissingFiles, Fingerprinted = c.Fingerprinted, FingerprintTarget = c.FingerprintTarget };
			}
			return arr;
		}

		// Per-drive concurrency: probe each drive's seek latency once; this threshold splits fast
		// (SSD/NVMe) from slow (spindle HDD). The DOP each drive gets is decided inline in GatherInfos.
		const double SsdHddLatencyThresholdMs = 3.0;
		// Median latency of a few random 64KB reads from one representative file on the drive. Random offsets
		// make it seek-bound so a spindle HDD separates cleanly from SSD/NVMe. null if nothing readable to probe.
		static double? ProbeSeekLatencyMs(System.Collections.Generic.IReadOnlyList<FileEntry> entries) {
			string? path = null;
			foreach (var e in entries) { if (e.FileSize > (1 << 20) && System.IO.File.Exists(e.Path)) { path = e.Path; break; } }
			if (path == null) return null;
			try {
				const int block = 64 * 1024, reads = 6;
				using var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite, block, System.IO.FileOptions.None);
				long len = fs.Length;
				if (len <= block) return null;
				var buf = new byte[block];
				var times = new System.Collections.Generic.List<double>(reads);
				var rnd = new Random(0x5eed);
				var sw = new System.Diagnostics.Stopwatch();
				for (int i = 0; i < reads; i++) {
					long off = (long)(rnd.NextDouble() * (len - block)) & ~4095L;
					fs.Seek(off, System.IO.SeekOrigin.Begin);
					sw.Restart();
					int n = fs.Read(buf, 0, block);
					sw.Stop();
					if (n > 0) times.Add(sw.Elapsed.TotalMilliseconds);
				}
				if (times.Count == 0) return null;
				times.Sort();
				return times[times.Count / 2];
			} catch { return null; }
		}

		string T(string key, params object[] args) =>
			LanguageService.Instance.Get(Settings.LanguageCode, key, args);

		// Status-bar label for the current phase. Empty during per-file analysis (which reports its
		// own sub-stages via ReportStage); set by the compare phases so the UI shows "comparing …"
		// instead of leaving a stale file path on screen.
		string currentStageLabel = string.Empty;

		// ParallelOptions rejects 0 and treats -1 as "unlimited"; the -1 default must pass through as
		// -1 (all cores). Math.Max(1, -1) was silently clamping it to 1 = single-threaded.
		int ParallelDegree => Settings.MaxDegreeOfParallelism == 0 ? -1 : Settings.MaxDegreeOfParallelism;

		void InitProgress(int count) {
			startTime = DateTime.UtcNow;
			scanProgressMaxValue = count;
			driveCounters = null;
			driveOrder = null;
			processedFiles = 0;
			preseededFiles = 0;   // gather re-seeds via BuildDriveCounters; compare phases start from zero
			lastProgressUpdate = DateTime.MinValue;
			lastCheckpointTime = DateTime.UtcNow;
		}

		// ETA from files processed THIS scan: the progress counters start pre-seeded with already-
		// complete work (cumulative bars), so the naive elapsed/processed rate would divide by
		// thousands of files this scan never touched and report a near-zero remaining time.
		TimeSpan EstimateRemaining() {
			int done = processedFiles - preseededFiles;
			if (done < 1) done = 1;
			long remaining = scanProgressMaxValue - processedFiles;
			if (remaining < 0) remaining = 0;
			return TimeSpan.FromTicks(DateTime.UtcNow.Subtract(startTime).Ticks * remaining / done);
		}
		void ResetExcludedLogging() {
			excludedReasonCounts.Clear();
			excludedReasonLoggedCounts.Clear();
		}
		void LogExcludedFile(FileEntry entry, string reason) {
			if (!Settings.LogExcludedFiles)
				return;
			var totalCount = excludedReasonCounts.AddOrUpdate(reason, 1, (_, count) => count + 1);
			var loggedCount = excludedReasonLoggedCounts.GetOrAdd(reason, 0);
			if (loggedCount >= maxExcludedLogsPerReason)
				return;
			loggedCount = excludedReasonLoggedCounts.AddOrUpdate(reason, 1, (_, count) => count + 1);
			if (loggedCount <= maxExcludedLogsPerReason)
				Logger.Instance.Info(T("Log.ExcludedFile", entry.Path, reason, totalCount));
		}
		void LogExcludedSummary() {
			if (!Settings.LogExcludedFiles || excludedReasonCounts.IsEmpty)
				return;
			Logger.Instance.Info(T("Log.ExcludedFilesSummary"));
			foreach (var reason in excludedReasonCounts.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)) {
				var loggedCount = excludedReasonLoggedCounts.TryGetValue(reason.Key, out var value) ? value : 0;
				var suppressedCount = Math.Max(0, reason.Value - loggedCount);
				var suppressionText = suppressedCount > 0 ? T("Log.ExcludedFilesSuppressed", suppressedCount) : string.Empty;
				Logger.Instance.Info(T("Log.ExcludedFilesSummaryItem", reason.Key, reason.Value, suppressionText));
			}
		}
		void IncrementProgress(string path, long fileSize = 0) {
			processedFiles++;
			if (fileSize != 0) {
				var __dc = driveCounters;
				if (__dc != null && __dc.TryGetValue(DriveRootOf(path), out var __c)) {
					System.Threading.Interlocked.Add(ref __c.DoneBytes, fileSize);
					System.Threading.Interlocked.Increment(ref __c.DoneFiles);
					// This thread is done with its current file on this drive; a no-op if it never had a slot
					// (e.g. a cached/skipped entry that returned before SetDriveCurrent was called).
					__c.Active.TryRemove(Environment.CurrentManagedThreadId, out _);
				}
			}
			// Periodic per-drive progress line so files/sec per drive can be read off the log (measurement/telemetry).
			if (driveCounters != null && lastDriveLog + progressLogInterval < DateTime.UtcNow) {
				lastDriveLog = DateTime.UtcNow;   // ponytail: racy across worker threads, worst case a duplicate line
				var __order = driveOrder;
				if (__order != null) {
					var __sb = new System.Text.StringBuilder("progress:");
					foreach (var __r in __order) { var __c2 = driveCounters[__r]; __sb.Append(' ').Append(__r).Append('=').Append(__c2.DoneFiles).Append('/').Append(__c2.TotalFiles); }
					Logger.Instance.Info(__sb.ToString());
				}
			}
			var pushUpdate = processedFiles == scanProgressMaxValue ||
								lastProgressUpdate + progressUpdateIntervall < DateTime.UtcNow;
			if (!pushUpdate) return;
			lastProgressUpdate = DateTime.UtcNow;
			var timeRemaining = EstimateRemaining();
			Progress?.Invoke(this,
							new ScanProgressChangedEventArgs {
								CurrentPosition = processedFiles,
								CurrentFile = path,
								Elapsed = ElapsedTimer.Elapsed,
								Remaining = timeRemaining,
								MaxPosition = scanProgressMaxValue,
								CurrentStage = currentStageLabel,
								Drives = DriveSnapshot(),
							});
			TryDatabaseCheckpoint();
		}

		// Reports what's happening to a file mid-processing without advancing the file counter.
		// Throttled to the same cadence as IncrementProgress so a stuck file's last-reported
		// stage (e.g. "sampling frame 2/5") hints at where it froze.
		void ReportStage(string path, string stage, int stageCurrent = 0, int stageMax = 0) {
			SetDriveCurrent(path, stage, stageCurrent, stageMax);   // per-drive row updates even when the global push below is throttled
			if (lastProgressUpdate + progressUpdateIntervall > DateTime.UtcNow) return;
			lastProgressUpdate = DateTime.UtcNow;
			var timeRemaining = EstimateRemaining();
			Progress?.Invoke(this,
							new ScanProgressChangedEventArgs {
								CurrentPosition = processedFiles,
								CurrentFile = path,
								Elapsed = ElapsedTimer.Elapsed,
								Remaining = timeRemaining,
								MaxPosition = scanProgressMaxValue,
								CurrentStage = stage,
								StageCurrent = stageCurrent,
								StageMax = stageMax,
								Drives = DriveSnapshot(),
							});
		}

		// Unthrottled progress push with no file payload of its own — called as a worker parks for a
		// pause. Its last completed file's throttled IncrementProgress push was usually swallowed
		// (the file's own ReportStage calls kept lastProgressUpdate fresh), so without this the UI
		// freezes on a stale row (e.g. "audio fingerprint 98/100") for the entire pause even though
		// that file actually finished. The last worker to park publishes the fully-drained truth.
		void PushProgressSnapshot() {
			lastProgressUpdate = DateTime.UtcNow;
			var timeRemaining = EstimateRemaining();
			Progress?.Invoke(this,
							new ScanProgressChangedEventArgs {
								CurrentPosition = processedFiles,
								CurrentFile = string.Empty,
								Elapsed = ElapsedTimer.Elapsed,
								Remaining = timeRemaining,
								MaxPosition = scanProgressMaxValue,
								CurrentStage = currentStageLabel,
								Drives = DriveSnapshot(),
							});
		}

		void TryDatabaseCheckpoint() {
			if (Settings.DatabaseCheckpointIntervalMinutes <= 0) return;
			var interval = TimeSpan.FromMinutes(Settings.DatabaseCheckpointIntervalMinutes);
			if (DateTime.UtcNow - lastCheckpointTime < interval) return;
			lock (checkpointLock) {
				// Re-check after acquiring lock to avoid duplicate saves from racing threads
				if (DateTime.UtcNow - lastCheckpointTime < interval) return;
				lastCheckpointTime = DateTime.UtcNow;
				// A checkpoint is best-effort: it runs on a worker thread inside the
				// hashing/compare loops, several of which only catch
				// OperationCanceledException. A failed periodic save must not abort the
				// whole scan — the final end-of-scan save is the one that has to succeed.
				try {
					DatabaseUtils.SaveDatabase();
					Logger.Instance.Info(T("Log.DatabaseCheckpoint", DatabaseUtils.Database.Count));
				}
				catch (Exception ex) {
					Logger.Instance.Info($"Database checkpoint failed (the scan continues; the final save still runs): {ex}");
				}
			}
		}

		// Explicit flush for a safe suspend point (Pause): persist completed work so the user can close/redeploy
		// and resume later via the fingerprint cache. Shares checkpointLock so it never races a periodic checkpoint
		// over the temp DB file. Best-effort; in-flight files finishing during the pause land in the next save.
		void FlushDatabase() {
			lock (checkpointLock) {
				lastCheckpointTime = DateTime.UtcNow;
				try {
					DatabaseUtils.SaveDatabase();
					Logger.Instance.Info("Paused: database flushed - safe to close (a later rescan resumes from the cache).");
				}
				catch (Exception ex) {
					Logger.Instance.Info($"Pause flush failed (the scan continues): {ex}");
				}
			}
		}

		public static bool FFmpegExists => !string.IsNullOrEmpty(FfmpegEngine.FFmpegPath);
		public static bool FFprobeExists => !string.IsNullOrEmpty(FFProbeEngine.FFprobePath);
		public static bool NativeFFmpegExists => FFTools.FFmpegNative.FFmpegHelper.DoFFmpegLibraryFilesExist;

		/// <param name="searchAndCompare">
		/// When true (GUI/Web default) the search chains straight into <see cref="StartCompare"/>.
		/// Callers that drive the two phases separately — the CLI runs hashing and comparison as
		/// distinct awaitable steps — must pass false, otherwise compare runs twice and the two
		/// concurrent <see cref="DatabaseUtils.SaveDatabase"/> calls race over the temp database
		/// file (#803).
		/// </param>
		public async void StartSearch(bool searchAndCompare = true) {
			PrepareSearch();
			SearchTimer.Start();
			ElapsedTimer.Start();
			Logger.Instance.InsertSeparator('-');
			Logger.Instance.Info(T("Log.BuildingFileList"));
			await BuildFileList(cancelationTokenSource.Token);
			Logger.Instance.Info(T("Log.FinishedBuildingFileList", SearchTimer.StopGetElapsedAndRestart()));
			FilesEnumerated?.Invoke(this, new EventArgs());
			Logger.Instance.Info(T("Log.GatheringMediaInfo"));
			if (!cancelationTokenSource.IsCancellationRequested)
				await GatherInfos();
			Logger.Instance.Info(T("Log.FinishedGatheringHashes", SearchTimer.StopGetElapsedAndRestart()));
			// Save before signaling completion: consumers (e.g. the CLI) may treat the
			// event as "done" and exit the process, which previously killed this thread
			// mid-write and left a torn ScannedFiles_new.db behind.
			try { DatabaseUtils.SaveDatabase(); }
			catch (Exception e) { Logger.Instance.Info($"WARNING: database save failed, keeping results in memory: {e.Message}"); }
			BuildingHashesDone?.Invoke(this, new EventArgs());
			if (!cancelationTokenSource.IsCancellationRequested && !stopRequested) {
				if (searchAndCompare)
					StartCompare();
				else
					isScanning = false; // search-only: no StartCompare to clear it
			}
			else {
				ScanAborted?.Invoke(this, new EventArgs());
				Logger.Instance.Info(T("Log.ScanAborted"));
				isScanning = false;
			}
		}

		public async void StartCompare() =>
			await RunCompare(runPHashCompare: true, runPartialCompare: Settings.EnablePartialClipDetection, clearDuplicates: true);

		/// <summary>
		/// Runs a single pipeline stage so the full scan can be driven one step at a time.
		/// BuildFileList/GatherInfos signal completion via <see cref="BuildingHashesDone"/>
		/// (the search-side "done" event, same as a searchAndCompare:false StartSearch);
		/// the compare stages via <see cref="ScanDone"/>. PartialCompare keeps the
		/// Duplicates found by a previous Compare stage — same accumulation as StartCompare
		/// running both phases in one go — so ①→②→③→④ reproduces a full scan.
		/// </summary>
		public async void StartStage(ScanStage stage) {
			switch (stage) {
				case ScanStage.BuildFileList:
					PrepareSearch();
					SearchTimer.Start();
					ElapsedTimer.Start();
					Logger.Instance.InsertSeparator('-');
					Logger.Instance.Info(T("Log.BuildingFileList"));
					await BuildFileList(cancelationTokenSource.Token);
					Logger.Instance.Info(T("Log.FinishedBuildingFileList", SearchTimer.StopGetElapsedAndRestart()));
					FilesEnumerated?.Invoke(this, new EventArgs());
					FinishSearchSideStage();
					break;
				case ScanStage.GatherInfos:
					PrepareSearch();
					SearchTimer.Start();
					ElapsedTimer.Start();
					Logger.Instance.InsertSeparator('-');
					// Standalone stage: BuildFileList (which normally loads the DB) may never have
					// run in this process — same fresh-process concern PrepareCompare handles (#790).
					// Off the UI thread: StartStage runs on the caller's (UI) thread until an await.
					if (DatabaseUtils.Database.Count == 0)
						await Task.Run(DatabaseUtils.LoadDatabase);
					Logger.Instance.Info(T("Log.GatheringMediaInfo"));
					if (!cancelationTokenSource.IsCancellationRequested)
						await GatherInfos();
					Logger.Instance.Info(T("Log.FinishedGatheringHashes", SearchTimer.StopGetElapsedAndRestart()));
					FinishSearchSideStage();
					break;
				case ScanStage.Compare:
					await RunCompare(runPHashCompare: true, runPartialCompare: false, clearDuplicates: true);
					break;
				case ScanStage.PartialCompare:
					await RunCompare(runPHashCompare: false, runPartialCompare: true, clearDuplicates: false);
					break;
				default:
					// Unreachable from the UI; fail visibly instead of leaving callers busy forever.
					ScanAborted?.Invoke(this, new EventArgs());
					break;
			}
		}

		// Shared tail of the search-side stages — mirrors StartSearch's searchAndCompare:false path.
		void FinishSearchSideStage() {
			// Save before signaling completion — see the matching comment in StartSearch.
			// A failed save (e.g. the DB file momentarily locked by an external reader while MoveFile
			// replaces it) must NOT skip the state reset below — otherwise the throw propagates out of
			// this async-void stage and the UI stays stuck in "stopping…" forever (observed 2026-07-06).
			// Keep results in memory, finish the stage, and let the next save retry.
			try { DatabaseUtils.SaveDatabase(); }
			catch (Exception e) { Logger.Instance.Info($"WARNING: database save failed, keeping results in memory: {e.Message}"); }
			if (cancelationTokenSource.IsCancellationRequested || stopRequested) {
				ScanAborted?.Invoke(this, new EventArgs());
				Logger.Instance.Info(T("Log.ScanAborted"));
			}
			else
				BuildingHashesDone?.Invoke(this, new EventArgs());
			isScanning = false;
		}

		async Task RunCompare(bool runPHashCompare, bool runPartialCompare, bool clearDuplicates) {
			try {
				PrepareCompare(clearDuplicates);
				SearchTimer.Start();
				ElapsedTimer.Start();
				Logger.Instance.Info(T("Log.ScanForDuplicates"));
				if (runPHashCompare && !cancelationTokenSource.IsCancellationRequested)
					await Task.Run(ScanForDuplicates, cancelationTokenSource.Token);
				if (runPartialCompare && !cancelationTokenSource.IsCancellationRequested)
					await Task.Run(ScanForPartialDuplicates, cancelationTokenSource.Token);
				SearchTimer.Stop();
				ElapsedTimer.Stop();
				Logger.Instance.Info(T("Log.FinishedScanForDuplicates", SearchTimer.Elapsed));
				LogGroupStatistics();
				Logger.Instance.Info(T("Log.HighlightingBestResults"));
				HighlightBestMatches();
				// Save before signaling completion — see the matching comment in StartSearch.
				DatabaseUtils.SaveDatabase();
				isScanning = false;
				ScanDone?.Invoke(this, new EventArgs());
				Logger.Instance.Info(T("Log.ScanDone"));
			}
			catch (Exception e) {
				// Callers are async void, so a throw here is swallowed by the global handler and
				// the GUI would stay busy forever (e.g. PrepareCompare's thumbnail-count guard, or
				// an OperationCanceledException when Stop lands mid-Parallel.For). Fail as an abort
				// instead so subscribers restore their state.
				Logger.Instance.Info($"Comparison aborted: {e.Message}");
				isScanning = false;
				ScanAborted?.Invoke(this, new EventArgs());
			}
		}

		void PrepareSearch() {
			ResetExcludedLogging();
			//Using VDF.GUI we know fftools exist at this point but VDF.Core might be used in other projects as well
			if (!Settings.UseNativeFfmpegBinding && !FFmpegExists)
				throw new FFNotFoundException("Cannot find FFmpeg");
			if (!FFprobeExists)
				throw new FFNotFoundException("Cannot find FFprobe");
			if (Settings.UseNativeFfmpegBinding && !FFTools.FFmpegNative.FFmpegHelper.DoFFmpegLibraryFilesExist)
				throw new FFNotFoundException($"Cannot find FFmpeg libraries. {FFTools.FFmpegNative.FFmpegHelper.DescribeExpectedLibraries()}");

			CancelAllTasks();

			FfmpegEngine.HardwareAccelerationMode = Settings.HardwareAccelerationMode;
			FfmpegEngine.CustomFFArguments = Settings.CustomFFArguments;
			FfmpegEngine.UseNativeBinding = Settings.UseNativeFfmpegBinding;
			FFTools.FFmpegNative.ParallelAudioFingerprinter.MaxDecodeThreads = Settings.ParallelAudioDecodeThreads;
			DatabaseUtils.CustomDatabaseFolder = Settings.CustomDatabaseFolder;
			DatabaseUtils.InvalidateDatabaseFolder();
			Duplicates.Clear();
			positionList.Clear();
			ElapsedTimer.Reset();
			SearchTimer.Reset();

			BuildPositionList();
			NormalizeScanPaths();

			isScanning = true;
		}

		void BuildPositionList() {
			positionList.Clear();
			float positionCounter = 0f;
			for (int i = 0; i < Settings.ThumbnailCount; i++) {
				positionCounter += 1.0F / (Settings.ThumbnailCount + 1);
				positionList.Add(positionCounter);
			}
		}

		/// <summary>
		/// FileEntry.Folder is always an absolute path without a trailing separator, but the
		/// include/blacklist entries arrive as typed (CLI flags, Web text fields, JSON settings).
		/// A relative path or trailing slash made the StartsWith inclusion check silently skip
		/// every database entry — scans found 0 duplicates with no hint why (issue #790).
		/// </summary>
		void NormalizeScanPaths() {
			static HashSet<string> Normalize(HashSet<string> paths) {
				var result = new HashSet<string>();
				foreach (var path in paths)
					result.Add(NormalizePathEntry(path));
				return result;
			}
			Settings.IncludeList = Normalize(Settings.IncludeList);
			Settings.BlackList = Normalize(Settings.BlackList);
		}

		// Normalizes one include/blacklist entry the way the scan compares them. Wildcard patterns
		// (#582) pass through verbatim: GetFullPath does NOT throw on '*'/'?' (they aren't
		// InvalidPathChars), so without the guard a bare pattern like "*temp*" silently gained a
		// CWD prefix and its segment matching broke. Public so the GUI directory tree hides
		// exactly what a scan skips.
		public static string NormalizePathEntry(string entry) {
			if (entry.IndexOfAny(['*', '?']) >= 0)
				return entry;
			try {
				return Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry));
			}
			catch { return entry; /* keep the original string if it cannot be resolved */ }
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		double GetGrayBytesIndex(FileEntry entry, float position) =>
			entry.GetGrayBytesIndex(position, Settings.MaxSamplingDurationSeconds);

		// clearDuplicates:false = the partial-compare-only stage, which appends its groups to the
		// visual-compare results already in Duplicates (also feeding its alreadyGrouped exclusion).
		void PrepareCompare(bool clearDuplicates = true) {
			if (positionList.Count == 0) {
				// Fresh process running compare-only (CLI 'compare' on an existing database):
				// the list is built during PrepareSearch, which never ran here (issue #790).
				BuildPositionList();
			}
			else if (Settings.ThumbnailCount != positionList.Count) {
				throw new Exception("Number of thumbnails can't be changed between quick rescans! Rescan has been aborted.");
			}
			NormalizeScanPaths();
			if (DatabaseUtils.Database.Count == 0) {
				// Compare-only in a fresh process: the database is normally loaded by
				// StartSearch's BuildFileList, which never ran in this process (issue #790).
				DatabaseUtils.CustomDatabaseFolder = Settings.CustomDatabaseFolder;
				DatabaseUtils.InvalidateDatabaseFolder();
				DatabaseUtils.LoadDatabase();
			}
			// entry.invalid defaults to true and is NOT persisted; it is only ever cleared by a
			// hashing pass (BuildFileList/GatherInfos). A compare-only run when the DB is ALREADY
			// in memory — the GUI preloads it at startup, and a completed/aborted gather leaves it
			// loaded — would otherwise see every entry as invalid and compare 0 files. Observed
			// 2026-07-07: clicking "시각 지문 비교" right after restart logged "Scanning for
			// duplicates in 0 files" three times while the DB held 17k analysed videos. Recompute
			// for every entry here, UNCONDITIONALLY, so compare always reflects the current scope/
			// filters. (Was gated on Count==0, which silently missed the preloaded-DB case.)
			foreach (FileEntry entry in DatabaseUtils.Database) {
				entry.invalid = InvalidEntry(entry, out _, out string? reason);
				if (entry.invalid && reason != null)
					LogExcludedFile(entry, reason);
			}

			CancelAllTasks();

			if (clearDuplicates)
				Duplicates.Clear();
			SearchTimer.Reset();
			if (!ElapsedTimer.IsRunning)
				ElapsedTimer.Reset();

			isScanning = true;
		}

		void CancelAllTasks() {
			if (!cancelationTokenSource.IsCancellationRequested)
				cancelationTokenSource.Cancel();
			cancelationTokenSource = new CancellationTokenSource();
			pauseTokenSource = new PauseTokenSource();
			stopRequested = false;
			isScanning = false;
		}

		Task BuildFileList(CancellationToken cancellationToken) => Task.Run(() => {

			DatabaseUtils.LoadDatabase();
			if (DatabaseUtils.DbVersion < 2)
				Settings.UsePHashing = false;

			int oldFileCount = DatabaseUtils.Database.Count;

			// Index existing analysed entries by size so a path-miss below can be checked for being a
			// MOVE (same content fingerprint, old path now gone) and relinked — reusing its analysis
			// instead of re-decoding. Only OsHash-bearing entries are relink targets; keyed by size so
			// we compute the new file's oshash only when a same-size analysed entry exists (zero reads
			// on a fresh scan, where the DB is empty).
			var relinkBySize = new Dictionary<long, List<FileEntry>>();
			foreach (var e in DatabaseUtils.Database)
				if (e.OsHash != null) {
					if (!relinkBySize.TryGetValue(e.FileSize, out var lst))
						relinkBySize[e.FileSize] = lst = new List<FileEntry>();
					lst.Add(e);
				}
			int relinkedCount = 0;

			foreach (string path in Settings.IncludeList) {
				if (cancellationToken.IsCancellationRequested)
					return;
				if (!Directory.Exists(path)) {
					// A disconnected network drive or removed folder would otherwise be
					// skipped without a trace, making the scan look broken (0 files found).
					Logger.Instance.Info($"WARNING: Search directory not found or inaccessible, skipping: '{path}'. If this is a network drive, make sure it is connected (or use the \\\\server\\share UNC path instead of a drive letter).");
					continue;
				}

				foreach (FileInfo file in FileUtils.GetFilesRecursive(path, Settings.IgnoreReadOnlyFolders, Settings.IgnoreReparsePoints,
					Settings.IncludeSubDirectories, Settings.IncludeImages, Settings.BlackList.ToList(), cancellationToken)) {
					if (cancellationToken.IsCancellationRequested)
						return;
					FileEntry fEntry;
					try {
						fEntry = new(file);
					}
					catch (Exception e) {
						//https://github.com/0x90d/videoduplicatefinder/issues/237
						Logger.Instance.Info($"Skipped file '{file}' because of {e}");
						continue;
					}
					if (!DatabaseUtils.Database.TryGetValue(fEntry, out var dbEntry)) {
						// Path not in the DB: either a genuinely new file or one moved/renamed from a
						// path that's now gone. Relink the latter so its analysis survives the move.
						if (TryRelinkMovedFile(fEntry, relinkBySize))
							relinkedCount++;
						else
							DatabaseUtils.Database.Add(fEntry);
					}
					else if (fEntry.FileSize != dbEntry.FileSize) {
						// Size changed -> content genuinely changed: drop stale analysis and re-decode.
						DatabaseUtils.Database.Remove(dbEntry);
						DatabaseUtils.Database.Add(fEntry);
					}
					else if (fEntry.DateCreated != dbEntry.DateCreated ||
							fEntry.DateModified != dbEntry.DateModified) {
						// Same size, only the timestamp moved. Could be a container-only rewrite
						// (faststart), a touch/copy/restore with identical bytes, or a rare same-size
						// content swap. Verify with the oshash before discarding phash/mediaInfo.
						string? os = OsHashUtils.TryCompute(fEntry.Path);
						if (os != null && dbEntry.OsHash != null && os != dbEntry.OsHash) {
							// Fingerprint differs -> different content at the same path -> re-analyze.
							DatabaseUtils.Database.Remove(dbEntry);
							DatabaseUtils.Database.Add(fEntry);
						}
						else {
							// Same (or unverifiable) fingerprint: same file, just re-dated. Keep the
							// cached analysis; only refresh timestamps so it won't re-trigger next scan.
							// ponytail: null oshash (pre-oshash entry or read fail) treated as same file to
							// stay append-only; ceiling is a same-size content swap on such an entry.
							dbEntry.DateCreated = fEntry.DateCreated;
							dbEntry.DateModified = fEntry.DateModified;
							if (dbEntry.OsHash == null && os != null)
								dbEntry.OsHash = os;
						}
					}
				}
			}

			Logger.Instance.Info($"Files in database: {DatabaseUtils.Database.Count:N0} ({DatabaseUtils.Database.Count - oldFileCount:N0} files added)");
			if (relinkedCount > 0)
				Logger.Instance.Info($"Detected {relinkedCount:N0} moved/renamed file(s) — reused existing analysis (no re-decode)");
		});

		// Returns true if fEntry is a moved/renamed version of an existing analysed entry — same size
		// and content fingerprint (oshash), and that entry's recorded path no longer exists — in which
		// case the existing entry is re-keyed to the new path, preserving grayBytes/mediaInfo/PHashes so
		// GatherInfos skips re-decoding it. Ambiguous matches (0 or >1 missing candidates with the same
		// oshash) fall through to "new file" so we never reuse the wrong data.
		bool TryRelinkMovedFile(FileEntry fEntry, Dictionary<long, List<FileEntry>> relinkBySize) {
			if (!relinkBySize.TryGetValue(fEntry.FileSize, out var sameSize))
				return false;
			// A move source is an entry whose recorded path is now gone. (A still-present path means it's
			// a copy, not a move — leave it and treat the new path as a new file.)
			List<FileEntry>? missing = null;
			foreach (var c in sameSize)
				if (!File.Exists(c.Path))
					(missing ??= new List<FileEntry>()).Add(c);
			if (missing == null)
				return false;

			string? oshash = OsHashUtils.TryCompute(fEntry.Path);
			if (oshash == null)
				return false;

			FileEntry? match = null;
			foreach (var c in missing)
				if (c.OsHash == oshash) {
					if (match != null)
						return false;   // more than one candidate with this fingerprint -> ambiguous, treat as new
					match = c;
				}
			if (match == null)
				return false;

			// Re-key the surviving entry to the new path. Its analysis rides along untouched; only the
			// path/date/size are refreshed so a later rescan at the new path won't flag it as modified.
			string oldPath = match.Path;
			DatabaseUtils.Database.Remove(match);
			match.Path = fEntry.Path;
			match.DateCreated = fEntry.DateCreated;
			match.DateModified = fEntry.DateModified;
			match.FileSize = fEntry.FileSize;
			DatabaseUtils.Database.Add(match);
			Logger.Instance.Info($"Moved file relinked (analysis reused): '{oldPath}' -> '{match.Path}'");
			return true;
		}

		// Scoped BuildFileList for a mid-stage drive re-enable: enumerates ONLY this drive's include
		// folders and adds files the last file-list build hasn't seen. NEW files only — size/timestamp
		// refresh and move-relink stay in the real BuildFileList stage. DB adds take checkpointLock so
		// a periodic checkpoint save never serializes the set mid-mutation.
		List<FileEntry> CollectNewFilesForDrive(string root, CancellationToken token) {
			var added = new List<FileEntry>();
			foreach (string path in Settings.IncludeList) {
				// stopRequested too: safe Stop only sets the flag (no token cancel), and "nothing
				// new starts" must cover a multi-minute directory walk, not just file claims.
				if (stopRequested || token.IsCancellationRequested) break;
				if (!string.Equals(DriveRootOf(path), root, StringComparison.OrdinalIgnoreCase)) continue;
				if (!Directory.Exists(path)) continue;
				foreach (FileInfo file in FileUtils.GetFilesRecursive(path, Settings.IgnoreReadOnlyFolders, Settings.IgnoreReparsePoints,
					Settings.IncludeSubDirectories, Settings.IncludeImages, Settings.BlackList.ToList(), token)) {
					if (stopRequested || token.IsCancellationRequested) break;
					FileEntry fEntry;
					try {
						fEntry = new(file);
					}
					catch (Exception e) {
						Logger.Instance.Info($"Skipped file '{file}' because of {e}");
						continue;
					}
					bool isNew;
					lock (checkpointLock) {
						isNew = !DatabaseUtils.Database.Contains(fEntry);
						if (isNew) DatabaseUtils.Database.Add(fEntry);
					}
					if (isNew) added.Add(fEntry);
				}
			}
			return added;
		}

		// Check if entry should be excluded from the scan for any reason
		// Returns true if the entry is invalid (should be excluded)
		bool InvalidEntry(FileEntry entry, out bool reportProgress, out string? reason) {
			reportProgress = true;
			reason = null;

			if (Settings.IncludeImages == false && entry.IsImage) {
				reason = "image files are disabled";
				return true;
			}
			if (Settings.BlackList.Any(f => IsBlackListed(entry.Folder, f))) {
				reason = "path is in the excluded directories list";
				return true;
			}

			if (!Settings.ScanAgainstEntireDatabase) {
				/* Skip non-included file before checking if it exists
				 * This greatly improves performance if the file is on
				 * a disconnected network/mobile drive
				 */
				if (Settings.IncludeSubDirectories == false) {
					if (!Settings.IncludeList.Contains(entry.Folder)) {
						reportProgress = false;
						reason = "path is not in the included directories list";
						return true;
					}
				}
				else if (!Settings.IncludeList.Any(f => {
					if (!entry.Folder.StartsWith(f))
						return false;
					if (entry.Folder.Length == f.Length)
						return true;
					//Reason: https://github.com/0x90d/videoduplicatefinder/issues/249
					string relativePath = Path.GetRelativePath(f, entry.Folder);
					return !relativePath.StartsWith('.') && !Path.IsPathRooted(relativePath);
				})) {
					reportProgress = false;
					reason = "path is not in the included directories list";
					return true;
				}
			}

			if (entry.Flags.Has(EntryFlags.ManuallyExcluded)) {
				reason = "file has been manually excluded";
				return true;
			}
			if (entry.Flags.Has(EntryFlags.TooDark)) {
				reason = "file is marked as too dark";
				return true;
			}
			if (!Settings.IncludeNonExistingFiles && !File.Exists(entry.Path))
			{
				reason = "file does not exist";
				return true;
			}
			if (!FileUtils.IsPathFFmpegSafe(entry.Path)) {
				entry.Flags.Set(EntryFlags.MetadataError);
				reason = "path contains characters not encodable to UTF-8 (e.g. lone surrogate from a mangled emoji) — FFmpeg cannot open it";
				return true;
			}

			if (Settings.FilterByFileSize && (entry.FileSize.BytesToMegaBytes() > Settings.MaximumFileSize ||
				entry.FileSize.BytesToMegaBytes() < Settings.MinimumFileSize)) {
				reason = "file size is outside the configured range";
				return true;
			}
			if (Settings.FilterByFilePathContains) {
				bool contains = false;
				foreach (var f in Settings.FilePathContainsTexts) {
					if (System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(f, entry.Path)) {
						contains = true;
						break;
					}
				}
				if (!contains) {
					reason = "file path does not match the required patterns";
					return true;
				}
			}

			if (Settings.IgnoreReparsePoints) {
				// The flag is stamped at FileEntry creation; entries from databases written
				// before it existed get a one-time stat here and carry the result forward.
				if (!entry.Flags.Has(EntryFlags.ReparsePointChecked)) {
					try {
						FileAttributes attributes = File.GetAttributes(entry.Path);
						entry.Flags.Set(EntryFlags.ReparsePoint, (attributes & FileAttributes.ReparsePoint) != 0);
						entry.Flags.Set(EntryFlags.ReparsePointChecked);
					}
					catch { } // missing/inaccessible file — the existence check above already covers it
				}
				if (entry.Flags.Has(EntryFlags.ReparsePoint)) {
					reason = "file is a reparse point";
					return true;
				}
			}
			if (Settings.FilterByFilePathNotContains) {
				bool contains = false;
				foreach (var f in Settings.FilePathNotContainsTexts) {
					if (System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(f, entry.Path)) {
						contains = true;
						break;
					}
				}
				if (contains) {
					reason = "file path matches an excluded pattern";
					return true;
				}
			}

			return false;
		}
		bool InvalidEntryForDuplicateCheck(FileEntry entry) =>
			entry.invalid || entry.mediaInfo == null || entry.Flags.Has(EntryFlags.ThumbnailError) || (!entry.IsImage && entry.grayBytes.Count < Settings.ThumbnailCount);

		public static Task<bool> LoadDatabase() => Task.Run(DatabaseUtils.LoadDatabase);
		public static void SaveDatabase() => DatabaseUtils.SaveDatabase();
		public static bool RemoveFromDatabase(FileEntry dbEntry) => DatabaseUtils.Database.Remove(dbEntry);
		// A DB entry can outlive its file. We tell an intentional deletion from a temporarily
		// offline drive by the file's ROOT: drive mounted but file gone = the user deleted it
		// (a "tombstone" whose fingerprint we keep so a re-download is recognized); drive itself
		// absent (USB unplugged, letter reassigned) = merely offline, must NOT count as deleted.
		// See TOMBSTONE-DESIGN.md.
		public static bool IsDriveReady(string path) {
			try {
				string? root = Path.GetPathRoot(path);
				if (string.IsNullOrEmpty(root))
					return false;                       // UNC / unrooted -> conservative: treat as offline
				return new DriveInfo(root).IsReady;
			}
			catch {
				return false;
			}
		}
		public static bool PathIsTombstone(string path) => !File.Exists(path) && IsDriveReady(path);
		public static bool PathIsOffline(string path) => !File.Exists(path) && !IsDriveReady(path);
		public static void UpdateFilePathInDatabase(string newPath, FileEntry dbEntry) => DatabaseUtils.UpdateFilePath(newPath, dbEntry);
#pragma warning disable CS8601 // Possible null reference assignment
		public static bool GetFromDatabase(string path, out FileEntry? dbEntry) {
			if (!File.Exists(path)) {
				dbEntry = null;
				return false;
			}
			return DatabaseUtils.Database.TryGetValue(new FileEntry(path), out dbEntry);
		}
#pragma warning restore CS8601 // Possible null reference assignment
		public static void BlackListFileEntry(string filePath) => DatabaseUtils.BlacklistFileEntry(filePath);

		// Returns true if folderPath is covered by blacklistEntry.
		// Supports wildcard patterns (*, ?) in blacklistEntry — see https://github.com/0x90d/videoduplicatefinder/issues/582
		// Public: the GUI directory tree uses the same rules to hide excluded folders.
		public static bool IsBlackListed(string folderPath, string blacklistEntry) {
			bool hasWildcard = blacklistEntry.IndexOfAny(['*', '?']) >= 0;
			if (!hasWildcard) {
				if (!folderPath.StartsWith(blacklistEntry, StringComparison.OrdinalIgnoreCase))
					return false;
				if (folderPath.Length == blacklistEntry.Length)
					return true;
				//Reason: https://github.com/0x90d/videoduplicatefinder/issues/249
				string relativePath = Path.GetRelativePath(blacklistEntry, folderPath);
				return !relativePath.StartsWith('.') && !Path.IsPathRooted(relativePath);
			}
			// Wildcard pattern without path separators: match against each individual segment of folderPath
			bool hasSeparator = blacklistEntry.Contains(Path.DirectorySeparatorChar) ||
			                    blacklistEntry.Contains(Path.AltDirectorySeparatorChar);
			if (!hasSeparator) {
				string[] segments = folderPath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
					StringSplitOptions.RemoveEmptyEntries);
				return segments.Any(s => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(blacklistEntry, s));
			}
			// Wildcard pattern with path separators: match against the full path
			return System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(blacklistEntry, folderPath);
		}

		// True if the entry's folder is covered by the current include list (honours IncludeSubDirectories).
		// Shared by the scope filter below and the OsHash backfill so out-of-scope drives are never read.
		bool IsInIncludeScope(FileEntry entry) {
			if (!Settings.IncludeSubDirectories)
				return Settings.IncludeList.Contains(entry.Folder);
			return Settings.IncludeList.Any(f => {
				if (!entry.Folder.StartsWith(f))
					return false;
				if (entry.Folder.Length == f.Length)
					return true;
				//Reason: https://github.com/0x90d/videoduplicatefinder/issues/249
				string relativePath = Path.GetRelativePath(f, entry.Folder);
				return !relativePath.StartsWith('.') && !Path.IsPathRooted(relativePath);
			});
		}

		// Adaptive per-drive concurrency with FAIR CPU-budget sharing. The bottleneck is CPU (decode), so total
		// workers are balanced around Environment.ProcessorCount and split across drives still working: each drive's
		// ceiling = max(8, ceil(cpuBudget / activeDrives)), recomputed as drives finish so survivors absorb the freed CPU.
		// A fixed worker pool is throttled to that ceiling, a shared global gate caps total decodes, and an AIMD
		// controller climbs toward the ceiling and backs a drive off only when its throughput drops (disk-bound).
		// A per-drive hard cap can be set live from the status-bar dropdown (SetDriveCap); 0 means fair-share up to the whole budget.
		async Task RunDriveAdaptive(string root, List<FileEntry> entries, Func<FileEntry, CancellationToken, ValueTask> process, System.Threading.SemaphoreSlim globalGate, int cpuBudget, int[] activeDrives) {
			var token = cancelationTokenSource.Token;
			double windowSec = Settings.AdaptiveWindowSeconds > 0 ? Settings.AdaptiveWindowSeconds : 120;
			int poolSize = Math.Max(1, cpuBudget);   // fixed worker pool; the live ceiling below throttles how many actually run
			// Fair-share ceiling, recomputed live each call so the user's per-drive dropdown cap (CapOverride,
			// 0 = fair-share up to the whole CPU budget) takes effect mid-scan within a few seconds.
			int FairCeiling() {
				int ov = 0;
				if (driveCounters != null && driveCounters.TryGetValue(root, out var __cap)) ov = __cap.CapOverride;
				if (ov > 0) return Math.Max(1, Math.Min(ov, cpuBudget));   // explicit per-drive override; total still capped by the global CPU gate
				int a = Math.Max(1, System.Threading.Volatile.Read(ref activeDrives[0]));
				// Auto floor of 8: with many drives active the pure fair share pins every drive low
				// (24 cpu / 6 drives = 4), starving fast NVMe drives that could use more. Total demand
				// may now exceed cpuBudget, but the global gate still caps actual concurrent decodes,
				// and AIMD backs a slow drive off its 8 as soon as its throughput drops.
				return Math.Max(Math.Min(8, cpuBudget), (cpuBudget + a - 1) / a);
			}
			int idx = -1;
			long done = 0;
			// Explicit (seeded/saved) cap: launch at exactly that many workers. Auto only: warm up at
			// min(fair share, 4) and let AIMD climb — the 4 is a cold-start heuristic, not a cap.
			bool hasExplicitCap = driveCounters != null && driveCounters.TryGetValue(root, out var __seed) && __seed.CapOverride > 0;
			int startC = hasExplicitCap ? FairCeiling() : Math.Max(1, Math.Min(FairCeiling(), 4));
			int target = startC;
			// AdaptiveThrottle (not a raw SemaphoreSlim): shrinking under CONTINUOUS full load can't steal
			// an idle permit (there never is one — each worker immediately re-acquires the permit it just
			// released for its next file), so a lowered cap must instead be consumed cooperatively by busy
			// workers as they each finish their current file. See AdaptiveThrottle.
			using var throttle = new AdaptiveThrottle(startC, poolSize);
			using var driveCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(token);
			// Files a mid-stage re-enable collected after this task's entries snapshot was taken.
			var lateQ = driveCounters != null && driveCounters.TryGetValue(root, out var __lq) ? __lq.LateArrivals : null;
			var workers = new List<Task>(poolSize);
			for (int w = 0; w < poolSize; w++) {
				workers.Add(Task.Run(async () => {
					while (!token.IsCancellationRequested) {
						if (stopRequested) break;   // safe stop: finish nothing new; current files already ran to 100%
						// Per-drive checkbox pause: park BEFORE claiming an index or any permit,
						// so in-flight files finish and no global decode slot is held while paused.
						if (DriveDisabled(root)) {
							// Once every still-running drive is paused, skip out instead of parking
							// forever — the stage ends when the checked drives finish; a paused
							// drive's leftovers wait for the next run.
							if (AllRemainingDrivesDisabled()) break;
							try { await Task.Delay(200, token).ConfigureAwait(false); }
							catch (OperationCanceledException) { break; }
							continue;
						}
						int i = System.Threading.Interlocked.Increment(ref idx);
						FileEntry? claimed = null;
						if (i < entries.Count) claimed = entries[i];
						// Snapshot exhausted: drain files a mid-stage re-enable collected after this
						// task started (live relist). The ConcurrentQueue hands each file to exactly
						// one worker; when it's empty too, this worker is done.
						else if (lateQ == null || !lateQ.TryDequeue(out claimed)) break;
						await throttle.WaitAsync(token).ConfigureAwait(false);
						bool retire;
						try {
							// Re-check AFTER the permit wait: every pool worker claims an index and
							// then queues on the throttle, so at cap 2 up to ~22 workers sit here for
							// minutes holding pre-claimed files. Without this check those stale claims
							// all ran to completion after the user unchecked the drive — 15+ minutes
							// of "paused" disk activity. Skipped claims are leftovers for the next run,
							// same semantics as parking. stopRequested gets the same treatment: safe
							// stop means in-flight files finish, not the whole queued backlog.
							if (!stopRequested && !DriveDisabled(root)) {
								await globalGate.WaitAsync(token).ConfigureAwait(false);
								try { await process(claimed, token).ConfigureAwait(false); }
								finally { globalGate.Release(); }
							}
						}
						finally { retire = throttle.Complete(); }
						System.Threading.Interlocked.Increment(ref done);
						if (retire) break;   // permit permanently retired; growth later reuses a still-idle spare worker instead
					}
				}, token));
			}
			var control = Task.Run(async () => {
				double prev = -1;
				long before = System.Threading.Interlocked.Read(ref done);
				double elapsed = 0;
				const double tick = 3.0;   // enforce a lowered cap within ~3s (live); measure throughput over a full window
				while (!driveCts.IsCancellationRequested) {
					try { await Task.Delay(TimeSpan.FromSeconds(tick), driveCts.Token).ConfigureAwait(false); }
					catch (OperationCanceledException) { break; }
					if (System.Threading.Volatile.Read(ref idx) >= entries.Count && (lateQ == null || lateQ.IsEmpty)) break;
					if (pauseTokenSource.IsPaused) { prev = -1; before = System.Threading.Interlocked.Read(ref done); elapsed = 0; continue; }

					var dcNow = driveCounters;
					int ov = 0;
					if (dcNow != null && dcNow.TryGetValue(root, out var __capNow)) ov = __capNow.CapOverride;
					if (ov > 0) {
						// Explicit user cap: this IS the target, applied directly every tick in either
						// direction — no AIMD creep/backoff second-guessing a value the user set on purpose
						// (the AIMD creep below only adds 1-2 workers per multi-minute window, far too slow
						// for a manual raise to actually take effect).
						int explicitTarget = Math.Max(1, Math.Min(ov, cpuBudget));
						if (target > explicitTarget) throttle.Shrink(target - explicitTarget);
						else if (target < explicitTarget) throttle.Grow(explicitTarget - target);
						target = explicitTarget;
						// Keep measuring throughput under an explicit cap — resetting the baseline
						// every tick left Rate permanently 0 for capped drives, so the status bar
						// showed a meaningless "0.0" instead of the drive's real pace.
						elapsed += tick;
						if (elapsed + 1e-9 >= windowSec) {
							long afterCapped = System.Threading.Interlocked.Read(ref done);
							if (driveCounters != null && driveCounters.TryGetValue(root, out var __rcCapped))
								__rcCapped.Rate = (afterCapped - before) / elapsed;
							before = afterCapped; elapsed = 0;
						}
						prev = -1;   // fresh AIMD baseline for when Auto resumes
						continue;
					}

					int ceiling = FairCeiling();
					// Live cap: fair-share ceiling dropped (another drive started) -> shed workers now, don't wait a window.
					if (target > ceiling) { throttle.Shrink(target - ceiling); target = ceiling; }
					elapsed += tick;
					if (elapsed + 1e-9 >= windowSec) {
						long after = System.Threading.Interlocked.Read(ref done);
						double rate = (after - before) / elapsed;
						int newTarget;
						if (prev >= 0 && rate < prev * 0.92) newTarget = target - 1;          // throughput dropped -> back off
						else if (prev >= 0 && rate > prev * 1.25) newTarget = target + 2;      // strong gain -> climb faster
						else newTarget = target + 1;                                           // else creep toward the ceiling
						newTarget = Math.Max(1, Math.Min(newTarget, ceiling));                 // never exceed the live ceiling
						if (target < newTarget) throttle.Grow(newTarget - target);
						else if (target > newTarget) throttle.Shrink(target - newTarget);
						target = newTarget;
						if (driveCounters != null && driveCounters.TryGetValue(root, out var __rc)) __rc.Rate = rate;
						Logger.Instance.Info($"[adaptive] {root}: {rate:0.000} files/s -> concurrency {target}/{ceiling} (active drives {System.Threading.Volatile.Read(ref activeDrives[0])})");
						prev = rate; before = after; elapsed = 0;
					}
				}
			}, driveCts.Token);
			try { await Task.WhenAll(workers).ConfigureAwait(false); }
			finally {
				System.Threading.Interlocked.Decrement(ref activeDrives[0]);   // release this drive's CPU share to the survivors
				lock (driveLifecycleLock) {
					runningDriveRoots?.TryRemove(root, out _);
					// A live relist can enqueue between the last worker's final dequeue check and
					// this teardown. Whatever it left behind gets a fresh drive task — the queue
					// hands each entry to either a worker or this drain, never both.
					if (lateQ != null && !lateQ.IsEmpty) gatherLateRespawn?.Invoke(root, lateQ);
				}
				driveCts.Cancel();
				try { await control.ConfigureAwait(false); } catch { }
			}
		}

		async Task GatherInfos() {
			try {
				currentStageLabel = string.Empty;
				// Progress universe = entries this scan will actually touch (see BuildDriveCounters);
				// with the full DB as the max, a narrowed scope could never reach 100%.
				int inScopeCount = 0;
				foreach (var e in DatabaseUtils.Database)
					if (Settings.ScanAgainstEntireDatabase || IsInIncludeScope(e)) inScopeCount++;
				InitProgress(inScopeCount);
				BuildDriveCounters();
				// Snapshot the content fingerprints (OsHash) of files that still exist, so 'Missing' (부재)
				// counts only UNIQUELY-deleted files: a gone entry whose content is still present elsewhere
				// under the same OsHash is a deleted DUPLICATE, not a loss, and must not inflate the count.
				// Off the UI thread — this stats every DB path (see the async-prefix rule). The matching
				// entries are also removed from the DB entirely by PruneRelocatedOrphans at scan end.
				var liveOsHashes = new HashSet<string>(StringComparer.Ordinal);
				await Task.Run(() => {
					foreach (var e in DatabaseUtils.Database)
						if (e.OsHash != null && IsDriveReady(e.Path) && File.Exists(e.Path))
							liveOsHashes.Add(e.OsHash);
				}).ConfigureAwait(false);
				ValueTask ProcessEntry(FileEntry entry, CancellationToken token) {
					// Park BEFORE the stop check: a worker sleeping through a pause has already claimed this
					// entry, and Stop-while-paused resumes it to wake it up — with the check first, every
					// woken worker then processed one extra file ("one more per drive after pressing Stop").
					if (pauseTokenSource.IsPaused) PushProgressSnapshot();
					pauseTokenSource.WaitWhilePaused(token);
					if (stopRequested) return ValueTask.CompletedTask;   // safe stop: not-yet-started entries are skipped (covers the static Parallel path too)

					// Entries complete at scan start were pre-counted into the bars by
					// BuildDriveCounters — their instant pass-through must not count a second time.
					// Same predicate both places, on fields the scan doesn't mutate for already-
					// complete entries, so the two classifications can't diverge.
					bool preCounted = EntryIsAlreadyComplete(entry);

					// Once per entry, when a real tool runs (ffprobe/frame sampling/audio fingerprint):
					// the per-drive "analyzed" count is what separates genuine work from the instant
					// cache/flag skips that fill a bar to 100% in seconds.
					bool analyzedCounted = false;
					void MarkAnalyzed() {
						if (analyzedCounted) return;
						analyzedCounted = true;
						var dcA = driveCounters;
						if (dcA != null && dcA.TryGetValue(DriveRootOf(entry.Path), out var cA))
							System.Threading.Interlocked.Increment(ref cA.Analyzed);
					}
					// Bumps the drive's cumulative fingerprint inventory when this entry just gained one.
					void MarkFingerprinted() {
						if (entry.AudioFingerprint == null) return;
						var dcF = driveCounters;
						if (dcF != null && dcF.TryGetValue(DriveRootOf(entry.Path), out var cF))
							System.Threading.Interlocked.Increment(ref cF.Fingerprinted);
					}

					try {
						entry.invalid = InvalidEntry(entry, out bool reportProgress, out string? invalidReason);
						if (entry.invalid && invalidReason != null)
							LogExcludedFile(entry, invalidReason);

						bool wasInvalid = entry.invalid;
						bool skipEntry = false;
						string? skipReason = null;
						skipEntry |= entry.invalid;
						if (!skipEntry && entry.Flags.Has(EntryFlags.ThumbnailError) &&
							SamplingPermanentlyFailed(true, Settings.AlwaysRetryFailedSampling, entry.SamplingFailCount, Settings.MaxSamplingRetryAttempts)) {
							skipEntry = true;
							skipReason = Settings.AlwaysRetryFailedSampling
								? $"frame sampling failed {entry.SamplingFailCount}x — retry budget exhausted, permanently skipped"
								: "previous thumbnail sampling failed and retry is disabled";
						}

						if (!skipEntry && !Settings.ScanAgainstEntireDatabase && !IsInIncludeScope(entry)) {
							skipEntry = true;
							skipReason = "path is not in the included directories list";
						}

						if (skipEntry) {
							entry.invalid = true;
							if (!wasInvalid && skipReason != null)
								LogExcludedFile(entry, skipReason);
							if (reportProgress && !preCounted)
								IncrementProgress(entry.Path, entry.FileSize);
							return ValueTask.CompletedTask;
						}

						if (!preCounted)
							SetDriveCurrent(entry.Path);   // real work starts here; skipped/cached/pre-counted entries never show

						// Cache a cheap content fingerprint so a future scan can detect this file was
						// MOVED (same OsHash, old path gone) and relink it without re-decoding. Runs once
						// per entry — computed here for new files and backfilled for pre-OsHash entries,
						// then persisted. Best-effort: a missing/locked file leaves it null.
						// Only fingerprint files inside the include list, so "scan against entire database"
						// (which compares every historical entry) never spins up out-of-scope drives for a read.
						if (entry.OsHash == null && IsInIncludeScope(entry))
							entry.OsHash = OsHashUtils.TryCompute(entry.Path);

						if (Settings.IncludeNonExistingFiles && entry.grayBytes?.Count > 0) {
							bool hasAllInformation = entry.IsImage;
							if (!hasAllInformation) {
								hasAllInformation = true;
								for (int i = 0; i < positionList.Count; i++) {
									if (entry.grayBytes.ContainsKey(GetGrayBytesIndex(entry, positionList[i])))
										continue;
									hasAllInformation = false;
									break;
								}
							}
							if (hasAllInformation) {
								// Thumbnails are cached but audio fingerprint might still be needed
								if (Settings.EnablePartialClipDetection &&
									!entry.IsImage &&
									!entry.Flags.Has(EntryFlags.NoAudioTrack) &&
									!entry.Flags.Has(EntryFlags.AudioFingerprintError) &&
									!entry.Flags.Has(EntryFlags.SilentAudioTrack) &&
									entry.AudioFingerprint == null) {
									string cachedAudioPath = entry.Path;
									string audioStageLabel = T("Scan.Stage.AudioFingerprint");
									ReportStage(cachedAudioPath, audioStageLabel);
									MarkAnalyzed();
									ExtractAudioFingerprint(entry, cancelationTokenSource.Token,
										onProgress: p => ReportStage(cachedAudioPath, audioStageLabel, (int)(p * 100), 100),
									maxFailAttempts: Settings.MaxSamplingRetryAttempts);
									MarkFingerprinted();
								}
								if (!preCounted)
									IncrementProgress(entry.Path, entry.FileSize);
								return ValueTask.CompletedTask;
							}
						}

						// Tombstone/offline safety: the file is gone (deleted, or its drive is unmounted).
						// A fully-cached entry was already kept above; one with incomplete cached data cannot
						// be (re)analysed without the file, so exclude it from this scan instead of spawning
						// ffprobe/ffmpeg on a missing path (which only errors).
						if (!File.Exists(entry.Path)) {
							entry.invalid = true;
							var dcM = driveCounters;
							// Count as 부재 only for a UNIQUE deletion: a gone entry whose content still exists
							// elsewhere under the same OsHash is a deleted duplicate, not a loss (user request).
							if (dcM != null && (entry.OsHash == null || !liveOsHashes.Contains(entry.OsHash)) &&
								dcM.TryGetValue(DriveRootOf(entry.Path), out var cM))
								System.Threading.Interlocked.Increment(ref cM.MissingFiles);
							if (!preCounted)
								IncrementProgress(entry.Path, entry.FileSize);
							return ValueTask.CompletedTask;
						}
						if (entry.mediaInfo == null && !entry.IsImage) {
							ReportStage(entry.Path, T("Scan.Stage.Probing"));
							MarkAnalyzed();
							MediaInfo? info = FFProbeEngine.GetMediaInfo(entry.Path, Settings.ExtendedFFToolsLogging);
							if (info == null) {
								entry.invalid = true;
								entry.Flags.Set(EntryFlags.MetadataError);
								IncrementProgress(entry.Path, entry.FileSize);
								return ValueTask.CompletedTask;
							}

							entry.mediaInfo = info;
						}
						// This is for people upgrading from an older VDF version
						// Or if you create a new database, start and immediately stop the scan and then try to scan again
						entry.grayBytes ??= new Dictionary<double, byte[]?>();
						entry.PHashes ??= new Dictionary<double, ulong?>();


						if (entry.IsImage && entry.grayBytes.Count == 0) {
							MarkAnalyzed();
							if (!GetGrayBytesFromImage(entry, Settings.UseExifCreationDate, Settings.ExtendedFFToolsLogging))
								entry.invalid = true;
						}
						else if (!entry.IsImage) {
							string entryPath = entry.Path;
							int totalSamples = positionList.Count;
							string samplingLabel = T("Scan.Stage.SamplingFrames");
							MarkAnalyzed();
							bool Sample() => FfmpegEngine.GetGrayBytesFromVideo(entry, positionList, Settings.MaxSamplingDurationSeconds,
									Settings.ExtendedFFToolsLogging,
									onSampleComplete: (done) => ReportStage(entryPath, samplingLabel, done, totalSamples));
							bool sampled = Sample();
							// Rescue for pre-fix cached probes: they lack VideoDurationSeconds, so a container
							// inflated by a long audio track / cover art seeks past the video's end and fails
							// on a good file. Re-probe once; if the fresh probe reveals a shorter video stream,
							// retry with the corrected (mapped) seeks before counting a failure.
							if (!sampled && entry.mediaInfo != null && entry.mediaInfo.VideoDurationSeconds == 0) {
								var fresh = FFProbeEngine.GetMediaInfo(entry.Path, Settings.ExtendedFFToolsLogging);
								if (fresh != null && fresh.VideoDurationSeconds > 0 &&
									fresh.VideoDurationSeconds < fresh.Duration.TotalSeconds) {
									entry.mediaInfo = fresh;
									entry.Flags.Set(EntryFlags.ThumbnailError, false);
									sampled = Sample();
								}
							}
							if (!sampled) {
								entry.invalid = true;
								// Count only genuine decode failures (ThumbnailError). TooDark is a real
								// property of the frames, not a retryable failure, so it must not burn the budget.
								if (entry.Flags.Has(EntryFlags.ThumbnailError))
									NoteSamplingFailure(entry);
							}
							else {
								// Fully sampled: clear any prior failure state so a healed file (e.g. an
								// earlier transient lock) stops being treated as incomplete.
								entry.Flags.Set(EntryFlags.ThumbnailError, false);
								entry.SamplingFailCount = 0;
							}
						}

						// Audio fingerprint — videos only, only when enabled,
						// skipped if already cached or flagged as having no audio track.
						if (Settings.EnablePartialClipDetection &&
							!entry.IsImage &&
							!entry.Flags.Has(EntryFlags.NoAudioTrack) &&
							!entry.Flags.Has(EntryFlags.AudioFingerprintError) &&
							!entry.Flags.Has(EntryFlags.SilentAudioTrack) &&
							entry.AudioFingerprint == null) {
							string audioPath = entry.Path;
							string audioLabel = T("Scan.Stage.AudioFingerprint");
							ReportStage(audioPath, audioLabel);
							MarkAnalyzed();
							ExtractAudioFingerprint(entry, cancelationTokenSource.Token,
								onProgress: p => ReportStage(audioPath, audioLabel, (int)(p * 100), 100),
								maxFailAttempts: Settings.MaxSamplingRetryAttempts);
							MarkFingerprinted();
						}

						if (!preCounted)
							IncrementProgress(entry.Path, entry.FileSize);
						return ValueTask.CompletedTask;
					}
					catch (OperationCanceledException) {
						throw;
					}
					catch (Exception ex) {
						// One bad file must not tear down a multi-hour scan. Flag the entry
						// so it's skipped on subsequent runs (unless AlwaysRetryFailedSampling)
						// and log enough detail to identify the culprit.
						Logger.Instance.Info($"Unhandled error processing '{entry.Path}': {ex}");
						entry.invalid = true;
						entry.Flags.Set(EntryFlags.ThumbnailError);
						NoteSamplingFailure(entry);
						if (!preCounted)
							IncrementProgress(entry.Path, entry.FileSize);
						return ValueTask.CompletedTask;
					}
				}

				// Group the DB by drive, probe each drive's seek latency once, then process each group at a concurrency
				// matched to its storage. Fast SSD/NVMe drives SHARE one CPU budget (= configured DOP, or
				// Environment.ProcessorCount when that is -1) split across them, so N fast drives don't spawn
				// N x ProcessorCount blocking decoders; each spindle HDD gets the low HDD DOP. Groups run concurrently
				// so a fast drive isn't blocked by a slow one — except at DOP=1, where we stay strictly serial.
				var byDrive = new Dictionary<string, List<FileEntry>>(StringComparer.OrdinalIgnoreCase);
				foreach (var e in DatabaseUtils.Database) {
					var r = DriveRootOf(e.Path);
					if (!byDrive.TryGetValue(r, out var lst)) { lst = new List<FileEntry>(); byDrive[r] = lst; }
					lst.Add(e);
				}
				// Probe each drive's seek latency once, up front — it now drives two decisions: the
				// LCN sort below (spinning only) and the static path's per-device DOP further down.
				// Drives with nothing in scope (no counter) are never read this scan, so don't spin
				// them up with a probe either.
				// The probe + FSCTL walk can take tens of seconds on big USB drives with no progress
				// events of their own — without the stage pushes below the window sits frozen and
				// reads as a hang.
				string lcnLabel = T("Scan.Stage.LcnSort");
				currentStageLabel = lcnLabel;
				PushProgressSnapshot();
				var seekLat = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
				// ★ The probe + FSCTL walk below are seconds-to-MINUTES of synchronous disk work, and
				// GatherInfos' pre-await prefix runs on the CALLER's thread — the UI thread for both
				// the full-scan and the stage buttons (async void command → first await). Without this
				// Task.Run the whole window genuinely froze for the entire walk: the progress events
				// fired but the dispatcher queue they marshal onto was the very thread doing the walk.
				// (A headless harness "worked", which is how this hid — no UI thread to block.)
				await Task.Run(() => {
				foreach (var kv in byDrive) {
					// Paused drives are skipped like out-of-scope ones: no probe, no LCN walk —
					// the checkbox exists precisely to keep that disk untouched.
					bool inScopeDrive = driveCounters != null && driveCounters.ContainsKey(kv.Key) && !DriveDisabled(kv.Key);
					if (inScopeDrive) ReportStage(kv.Key, lcnLabel);
					seekLat[kv.Key] = inScopeDrive ? ProbeSeekLatencyMs(kv.Value) : null;
				}
				// Order each SPINNING drive's queue by on-disk position (first-extent LCN) so its
				// single head assembly sweeps the platter once instead of seeking randomly between
				// consecutive files — database iteration order is effectively random, the worst case
				// for a mechanical drive. NAND/SSD drives skip the sort: random access costs nothing
				// there, so the per-file FSCTL queries would be pure waste. Classification reuses the
				// empirical probe (the storage MediaType API reports "Unspecified" behind USB bridges).
				if (OperatingSystem.IsWindows()) {
					foreach (var kv in byDrive) {
						if (driveCounters == null || !driveCounters.ContainsKey(kv.Key)) continue;   // nothing in scope: never read this scan
						if (DriveDisabled(kv.Key)) continue;   // paused via checkbox: don't touch the disk
						double? lat = seekLat[kv.Key];
						if (lat == null || lat.Value < SsdHddLatencyThresholdMs) {
							Logger.Instance.Info($"[lcn] {kv.Key}: fast/unprobed (seek {(lat.HasValue ? lat.Value.ToString("0.0") : "?")}ms) — keeping database order");
							continue;
						}
						var lcnSw = System.Diagnostics.Stopwatch.StartNew();
						var keyed = new List<(long Key, FileEntry E)>(kv.Value.Count);
						foreach (var e in kv.Value) {
							if (cancelationTokenSource.IsCancellationRequested) break;
							if (DriveDisabled(kv.Key)) break;   // unchecked mid-walk: stop touching this disk now
							if ((keyed.Count & 127) == 0)
								ReportStage(e.Path, lcnLabel, keyed.Count, kv.Value.Count);
							keyed.Add((LcnUtils.GetFirstLcn(e.Path), e));
						}
						if (keyed.Count != kv.Value.Count) {   // aborted mid-walk: keep original order
							if (cancelationTokenSource.IsCancellationRequested) break;   // whole scan is stopping
							continue;   // just this drive unchecked — the others still get their sort
						}
						keyed.Sort((a, b) => a.Key.CompareTo(b.Key));
						for (int i = 0; i < keyed.Count; i++) kv.Value[i] = keyed[i].E;
						Logger.Instance.Info($"[lcn] {kv.Key}: spinning (seek {lat.Value:0.0}ms) — ordered {kv.Value.Count:N0} entries by on-disk position in {lcnSw.ElapsedMilliseconds:N0}ms");
					}
				}
				// Clear the sort's status rows (this thread has a slot in every probed drive's Active
				// dict, and the workers run on OTHER threads so nothing else would ever remove them),
				// then push the clean state before processing starts. Must stay INSIDE the Task.Run:
				// the slots are keyed by the walking thread's id.
				if (driveCounters != null)
					foreach (var dcKv in driveCounters)
						dcKv.Value.Active.TryRemove(Environment.CurrentManagedThreadId, out _);
				currentStageLabel = string.Empty;
				PushProgressSnapshot();
				}, cancelationTokenSource.Token).ConfigureAwait(false);
				// Adaptive path: each drive self-tunes its concurrency from live files/sec (global CPU-capped). Static
				// per-device split is the fallback (AdaptiveConcurrency off, or DOP=1 which stays strictly serial).
				if (Settings.AdaptiveConcurrency && Settings.MaxDegreeOfParallelism != 1 && byDrive.Count > 0) {
					int cpuCap = Environment.ProcessorCount;
					using var globalGate = new System.Threading.SemaphoreSlim(cpuCap, cpuCap);
					int[] activeDrives = { byDrive.Count };
					runningDriveRoots = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
					foreach (var akv in byDrive) runningDriveRoots[akv.Key] = 0;
					Logger.Instance.Info($"Adaptive per-drive concurrency (fair CPU share): budget {cpuCap} across {byDrive.Count} drive(s), window {(Settings.AdaptiveWindowSeconds > 0 ? Settings.AdaptiveWindowSeconds : 120)}s");

					// ── Live relist: re-enabling a drive mid-stage picks up files created since the
					// last file-list build. The relist runs while the drive still reads as Disabled
					// (workers keep parking, AllRemainingDrivesDisabled treats the pending relist as
					// active), lands its new files, THEN applies the user's checkbox state. lateTasks
					// collects relist tasks and late-respawned drive tasks; the wave loop below keeps
					// awaiting until nothing new arrives. All shared state under driveLifecycleLock.
					bool accepting = true;
					var lateTasks = new List<Task>();
					void RelistDrive(string root, DriveCounter c) {
						int found = 0;
						try {
							var addedList = CollectNewFilesForDrive(root, cancelationTokenSource.Token);
							found = addedList.Count;
							// On safe Stop: the files are in the database already — leave the totals and
							// the handoff alone (bumping the max after Stop made the bar jump backwards,
							// and a respawned task's workers would all break instantly anyway).
							if (found > 0 && !stopRequested) {
								long addedBytes = 0; int fpTargets = 0;
								foreach (var e in addedList) {
									addedBytes += e.FileSize;
									if (Settings.EnablePartialClipDetection && !e.IsImage) fpTargets++;
								}
								System.Threading.Interlocked.Add(ref c.TotalFiles, found);
								System.Threading.Interlocked.Add(ref c.TotalBytes, addedBytes);
								System.Threading.Interlocked.Add(ref c.FingerprintTarget, fpTargets);
								lock (driveLifecycleLock) {
									scanProgressMaxValue += found;
									if (runningDriveRoots != null && runningDriveRoots.ContainsKey(root)) {
										// Drive task still alive (workers parked behind Disabled): feed its queue.
										foreach (var e in addedList) c.LateArrivals.Enqueue(e);
									}
									else if (accepting) {
										// Drive already finished its snapshot: give the new files their own task.
										runningDriveRoots![root] = 0;
										System.Threading.Interlocked.Increment(ref activeDrives[0]);
										lateTasks.Add(RunDriveAdaptive(root, addedList, ProcessEntry, globalGate, cpuCap, activeDrives));
									}
									else
										Logger.Instance.Info($"[relist] {root}: stage ended first — {found} new file(s) are in the database and will be processed next run");
								}
							}
							Logger.Instance.Info($"[relist] {root}: re-enabled — {found} new file(s) since the last file-list build");
						}
						catch (Exception ex) {
							Logger.Instance.Info($"[relist] {root} failed (drive resumes with its existing queue): {ex}");
						}
						finally {
							// Same lock as SetDriveEnabled: the apply must be atomic against a
							// concurrent toggle or the toggle is silently lost (see SetDriveEnabled).
							// Disabled before the pending flag, so the drive never reads as fully
							// parked mid-handoff.
							lock (driveLifecycleLock) {
								c.Disabled = !c.DesiredEnabled;
								System.Threading.Interlocked.Exchange(ref c.RelistPending, 0);
							}
							PushProgressSnapshot();
						}
					}
					gatherLiveRelist = (root, c) => {
						lock (driveLifecycleLock) {
							if (!accepting) {
								c.Disabled = !c.DesiredEnabled;
								System.Threading.Interlocked.Exchange(ref c.RelistPending, 0);
								return;
							}
							lateTasks.Add(Task.Run(() => RelistDrive(root, c)));
						}
					};
					gatherLateRespawn = (root, q) => {   // invoked under driveLifecycleLock (task teardown)
						var orphans = new List<FileEntry>();
						while (q.TryDequeue(out var e)) orphans.Add(e);
						if (orphans.Count == 0) return;
						if (!accepting || stopRequested) {
							Logger.Instance.Info($"[relist] {root}: stage ended before {orphans.Count} late file(s) could run — they will be processed next run");
							return;
						}
						runningDriveRoots![root] = 0;
						System.Threading.Interlocked.Increment(ref activeDrives[0]);
						lateTasks.Add(RunDriveAdaptive(root, orphans, ProcessEntry, globalGate, cpuCap, activeDrives));
						Logger.Instance.Info($"[relist] {root}: respawned drive task for {orphans.Count} late file(s)");
					};
					try {
						var wave = new List<Task>(byDrive.Count);
						foreach (var akv in byDrive) wave.Add(RunDriveAdaptive(akv.Key, akv.Value, ProcessEntry, globalGate, cpuCap, activeDrives));
						while (true) {
							await Task.WhenAll(wave);
							lock (driveLifecycleLock) {
								if (lateTasks.Count == 0) { accepting = false; break; }
								wave = new List<Task>(lateTasks);
								lateTasks.Clear();
							}
						}
					}
					finally {
						// Exception/cancel path: stop accepting, then drain in-flight relist tasks so
						// none of them mutates the database while the stage-end save serializes it.
						List<Task>? drain = null;
						lock (driveLifecycleLock) {
							accepting = false;
							if (lateTasks.Count > 0) { drain = new List<Task>(lateTasks); lateTasks.Clear(); }
						}
						gatherLiveRelist = null;
						gatherLateRespawn = null;
						if (drain != null)
							try { await Task.WhenAll(drain).ConfigureAwait(false); } catch { }
					}
				}
				else {
				int ssdDop = Settings.MaxDegreeOfParallelism;                       // -1, or a positive total CPU budget
				int hddDop = Settings.HddMaxDegreeOfParallelism > 0 ? Settings.HddMaxDegreeOfParallelism : 2;
				bool forceSerial = ssdDop == 1;                                     // user pinned single-thread -> honour globally
				var groups = new List<(string Root, List<FileEntry> Entries, double? Lat)>();
				foreach (var kv in byDrive)
					groups.Add((kv.Key, kv.Value, seekLat[kv.Key]));   // probed once above, before the LCN sort
				int fastCount = 0;
				foreach (var g in groups) if (g.Lat != null && g.Lat.Value < SsdHddLatencyThresholdMs) fastCount++;
				int fastBudget = ssdDop < 0 ? Environment.ProcessorCount : ssdDop;
				int fastPerGroup = fastCount > 0 ? Math.Max(1, fastBudget / fastCount) : Math.Max(1, fastBudget);
				int DopFor(double? lat) {
					if (forceSerial) return 1;
					if (lat == null) return 4;                                     // unprobeable -> safe moderate
					return lat.Value >= SsdHddLatencyThresholdMs ? hddDop : fastPerGroup;
				}
				// Static-path variant of the per-drive checkbox pause: the adaptive path parks in
				// its own worker loop; here each drive's ForEachAsync holds no cross-drive permits,
				// so waiting inside the body only blocks this drive's own slots. When every
				// still-running drive is paused, entries are skipped so the stage can end.
				async ValueTask GatedProcess(string root, FileEntry entry, CancellationToken tk) {
					while (DriveDisabled(root) && !stopRequested && !tk.IsCancellationRequested) {
						if (AllRemainingDrivesDisabled()) return;   // skip — paused leftovers wait for the next run
						await Task.Delay(200, tk).ConfigureAwait(false);
					}
					if (DriveDisabled(root)) return;
					await ProcessEntry(entry, tk).ConfigureAwait(false);
				}
				async Task RunStaticGroup(string root, List<FileEntry> entries, int dop) {
					try {
						await Parallel.ForEachAsync(entries, new ParallelOptions { CancellationToken = cancelationTokenSource.Token, MaxDegreeOfParallelism = dop }, (e, tk) => GatedProcess(root, e, tk));
					}
					finally {
						runningDriveRoots?.TryRemove(root, out _);
					}
				}
				runningDriveRoots = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
				foreach (var g in groups) runningDriveRoots[g.Root] = 0;
				if (forceSerial) {
					// preserve the pre-stage-3 single-thread guarantee: one drive, one file at a time
					foreach (var g in groups)
						await RunStaticGroup(g.Root, g.Entries, 1);
				}
				else {
					var driveTasks = new List<Task>(groups.Count);
					foreach (var g in groups) {
						int dop = DopFor(g.Lat);
						Logger.Instance.Info($"Drive {g.Root}: {g.Entries.Count:N0} file(s), concurrency = {dop}" + (g.Lat != null ? $" (seek {g.Lat.Value:0.0}ms)" : " (unprobed)"));
						driveTasks.Add(RunStaticGroup(g.Root, g.Entries, dop));
					}
					await Task.WhenAll(driveTasks);
				}
				}
			}
			catch (OperationCanceledException) { }
			finally {
				LogExcludedSummary();
			}
			// Post-backfill DB hygiene: in-scope live entries now carry OsHash and gone entries are
			// identified, so drop any stale duplicate the relink missed (moved file whose content is
			// already present elsewhere). Clean finish only — a cancelled stage may not have backfilled
			// every twin, which would make a present copy look absent and spare a real orphan.
			if (!cancelationTokenSource.IsCancellationRequested && !stopRequested) {
				PruneRelocatedOrphans();
				// Opt-in destructive cleanup: recycle videos that never decoded after the full retry
				// budget (genuinely corrupt). Off by default; recycle bin only, every file logged.
				if (Settings.AutoDeleteUnrecoverableFiles)
					AutoDeleteUnrecoverableVideos(Settings.MaxSamplingRetryAttempts);
			}
		}

	
	internal static void ExtractAudioFingerprint(FileEntry entry, CancellationToken ct = default, Action<double>? onProgress = null, int maxFailAttempts = 1) {
		uint[]? fp = FFTools.ChromaprintEngine.ExtractFingerprint(entry.Path, false, ct, onProgress);
		if (fp == null && ct.IsCancellationRequested) {
			// Stop/cancel mid-file is not a file error. Flagging here poisoned the entry
			// permanently: both the AudioFingerprintError flag and the non-null empty
			// fingerprint block every retry gate (EntryIsAlreadyComplete and the
			// ProcessEntry audio checks), so the file would never be fingerprinted again.
			// Leave the entry untouched and let the next scan retry it.
			return;
		}
		if (fp == null) {
			// Extraction failed. Mirror the visual-sampling retry budget: give it a few chances
			// (transient lock/offline) before marking it permanently failed, instead of the old
			// immediate-permanent skip. Leave AudioFingerprint null until the budget is spent so the
			// next scan retries; the count rides on the entry (survives a relinked move).
			if (entry.AudioFingerprintFailCount < byte.MaxValue) entry.AudioFingerprintFailCount++;
			if (maxFailAttempts > 0 && entry.AudioFingerprintFailCount >= maxFailAttempts) {
				entry.Flags.Set(EntryFlags.AudioFingerprintError);
				entry.AudioFingerprint = Array.Empty<uint>();
			}
			return;
		}
		entry.AudioFingerprintFailCount = 0;   // a definitive result below clears the retry budget
		if (fp.Length == 0) {
			// FFmpeg ran but produced no samples (file has no usable audio)
			entry.Flags.Set(EntryFlags.NoAudioTrack);
			entry.AudioFingerprint = Array.Empty<uint>();
		}
		else if (IsSilentFingerprint(fp)) {
			// Silent tracks produce all-zero fingerprints, which Hamming-match any
			// other silent track at 100% and cause false-positive partial-clip groups.
			entry.Flags.Set(EntryFlags.SilentAudioTrack);
			entry.AudioFingerprint = Array.Empty<uint>();
		}
		else {
			entry.AudioFingerprint = fp;
		}
	}

	/// <summary>
	/// Returns true when every block in the fingerprint is zero. For silent or
	/// near-silent audio the chroma bins collapse to equal values, and the 32
	/// comparison pairs in <see cref="Chromaprint.Pipeline.FingerprintCalculator"/>
	/// all resolve to the non-greater branch, producing uniformly zero blocks.
	/// </summary>
	internal static bool IsSilentFingerprint(uint[] fp) {
		if (fp.Length == 0) return false;
		for (int i = 0; i < fp.Length; i++)
			if (fp[i] != 0u) return false;
		return true;
	}

	static byte[]?[] CreateFlippedGrayBytes(FileEntry entry) {
			byte[]?[] source = entry.compareGray!;
			var flipped = new byte[]?[source.Length];
			for (int j = 0; j < source.Length; j++)
				// FlipGrayScale derives the side length from the array, so it handles both
				// current 32x32 data and 16x16 data from legacy (DbVersion < 2) databases.
				flipped[j] = GrayBytesUtils.FlipGrayScale(source[j]!);
			return flipped;
		}

		/// <summary>Returns true if the last <paramref name="depth"/> path segments of both folder paths are equal (case-insensitive).</summary>
	static bool SameFolderAtDepth(ReadOnlySpan<char> a, ReadOnlySpan<char> b, int depth) {
		for (int i = 0; i < depth; i++) {
			while (a.Length > 0 && (a[^1] == Path.DirectorySeparatorChar || a[^1] == Path.AltDirectorySeparatorChar))
				a = a[..^1];
			while (b.Length > 0 && (b[^1] == Path.DirectorySeparatorChar || b[^1] == Path.AltDirectorySeparatorChar))
				b = b[..^1];

			int sepA = a.LastIndexOf(Path.DirectorySeparatorChar);
			if (Path.DirectorySeparatorChar != Path.AltDirectorySeparatorChar) {
				int alt = a.LastIndexOf(Path.AltDirectorySeparatorChar);
				if (alt > sepA) sepA = alt;
			}
			int sepB = b.LastIndexOf(Path.DirectorySeparatorChar);
			if (Path.DirectorySeparatorChar != Path.AltDirectorySeparatorChar) {
				int alt = b.LastIndexOf(Path.AltDirectorySeparatorChar);
				if (alt > sepB) sepB = alt;
			}

			var segA = sepA >= 0 ? a[(sepA + 1)..] : a;
			var segB = sepB >= 0 ? b[(sepB + 1)..] : b;

			if (!segA.Equals(segB, StringComparison.OrdinalIgnoreCase))
				return false;

			a = sepA >= 0 ? a[..sepA] : ReadOnlySpan<char>.Empty;
			b = sepB >= 0 ? b[..sepB] : ReadOnlySpan<char>.Empty;
		}
		return true;
	}

	void LogMissingPHash(string path) {
			if (missingPHashFiles.TryAdd(path, 0))
				Logger.Instance.Info($"Missing pHash data for '{path}' — file will be skipped in pHash comparisons. Re-scan to repopulate.");
		}

	/// <summary>
		/// Builds the transient compare snapshot for <paramref name="entry"/>: gray-byte
		/// arrays aligned with <see cref="positionList"/> order and, when pHashing is
		/// enabled, the first-position pHash (computed once and cached back into
		/// <see cref="FileEntry.PHashes"/> if it was missing). Returns false when the
		/// stored data is incomplete for the current scan settings — those entries are
		/// excluded from the comparison instead of failing on every pair.
		/// </summary>
		bool TryBuildCompareSnapshot(FileEntry entry, bool usePHashing) {
			if (entry.IsImage) {
				if (!entry.grayBytes.TryGetValue(0, out byte[]? imageGray) || imageGray == null)
					return false;
				entry.compareGray = new[] { imageGray };
				return true;
			}

			var gray = new byte[]?[positionList.Count];
			for (int j = 0; j < positionList.Count; j++) {
				double idx = GetGrayBytesIndex(entry, positionList[j]);
				if (!entry.grayBytes.TryGetValue(idx, out byte[]? data) || data == null)
					return false;
				gray[j] = data;
			}
			entry.compareGray = gray;

			if (usePHashing) {
				double idx0 = GetGrayBytesIndex(entry, positionList[0]);
				if (!entry.PHashes.TryGetValue(idx0, out ulong? phash)) {
					phash = pHash.PerceptualHash.ComputePHashFromGray32x32(gray[0]);
					entry.PHashes[idx0] = phash; // cache for future quick rescans
				}
				if (phash == null)
					LogMissingPHash(entry.Path);
				entry.comparePHash = phash;
			}
			return true;
		}

		bool CheckIfDuplicate(FileEntry entry, byte[]?[]? overrideGray, ulong? overridePHash, FileEntry compItem, out float difference) {
			byte[]?[] grayBytes = overrideGray ?? entry.compareGray!;
			float differenceLimit = 1.0f - Settings.Percent / 100f;
			bool ignoreBlackPixels = Settings.IgnoreBlackPixels;
			bool ignoreWhitePixels = Settings.IgnoreWhitePixels;
			difference = 1f;

			if (entry.IsImage) {
				difference = ignoreBlackPixels || ignoreWhitePixels ?
								GrayBytesUtils.PercentageDifferenceWithoutSpecificPixels(grayBytes[0]!, compItem.compareGray![0]!, ignoreBlackPixels, ignoreWhitePixels) :
								GrayBytesUtils.PercentageDifference(grayBytes[0]!, compItem.compareGray![0]!);
				return difference <= differenceLimit;
			}

			if (Settings.UsePHashing) {
				float differenceLimitpHash = Settings.Percent / 100f;

				// Entries with unrecoverable pHash data were logged once during
				// snapshot building; they simply never match in pHash mode.
				ulong? phash = overrideGray != null ? overridePHash : entry.comparePHash;
				ulong? phash_comp = compItem.comparePHash;
				if (phash == null || phash_comp == null) {
					difference = 1f;
					return false;
				}
				bool isDup = pHash.PHashCompare.IsDuplicateByPercent(phash.Value, phash_comp.Value, out float similarity, differenceLimitpHash, strict: true);
				difference = 1f - similarity;
				return isDup;

			}

			byte[]?[] compGray = compItem.compareGray!;
			differenceLimit *= grayBytes.Length;
			float diffSum = 0;
			for (int j = 0; j < grayBytes.Length; j++) {
				diffSum += ignoreBlackPixels || ignoreWhitePixels ?
							GrayBytesUtils.PercentageDifferenceWithoutSpecificPixels(
								grayBytes[j]!, compGray[j]!, ignoreBlackPixels, ignoreWhitePixels) :
							GrayBytesUtils.PercentageDifference(grayBytes[j]!, compGray[j]!);
				if (diffSum > differenceLimit) // already exceeding maximum tolerated diff -> exit early
					return false;
			}
			difference = diffSum / grayBytes.Length;
			return !float.IsNaN(difference);
		}

		internal void ScanForDuplicates() {
			Dictionary<string, DuplicateItem>? duplicateDict = new();
			// Maps GroupId -> representative FileEntry for that group.
			// Used to prevent merging groups whose representatives aren't similar.
			Dictionary<Guid, FileEntry> groupRepresentatives = new();
			// Maps GroupId -> its members, so merging two groups relabels only the
			// absorbed group's items instead of scanning every duplicate found so far
			// while holding the lock.
			Dictionary<Guid, List<DuplicateItem>> groupMembers = new();
			int mergesBlocked = 0;
			missingPHashFiles.Clear();

			//Exclude existing database entries which not met current scan settings
			List<FileEntry> ScanList = new();

			Logger.Instance.Info("Prepare list of items to compare...");
			foreach (FileEntry entry in DatabaseUtils.Database) {
				if (!InvalidEntryForDuplicateCheck(entry)) {
					ScanList.Add(entry);
				}
			}

			// Materialize per-entry compare snapshots so the per-pair hot path works on
			// plain arrays instead of probing Dictionary<double,...> with recomputed keys.
			// Entries whose stored data is incomplete for the current settings are dropped
			// here (previously they would have failed mid-comparison on every pair).
			bool usePHashing = Settings.UsePHashing;
			int droppedSnapshots = 0;
			{
				List<FileEntry> validated = new(ScanList.Count);
				foreach (FileEntry entry in ScanList) {
					if (TryBuildCompareSnapshot(entry, usePHashing)) {
						// compareIndex preserves list ordering so symmetric comparisons can be skipped.
						entry.compareIndex = validated.Count;
						validated.Add(entry);
					}
					else
						droppedSnapshots++;
				}
				ScanList = validated;
			}
			if (droppedSnapshots > 0)
				Logger.Instance.Info($"Excluded {droppedSnapshots} file(s) with incomplete cached scan data (missing gray bytes for the current thumbnail positions). Rescan to repopulate.");

			Logger.Instance.Info($"Scanning for duplicates in {ScanList.Count:N0} files");

			InitProgress(ScanList.Count);
			currentStageLabel = T("Scan.Stage.ComparingDuplicates");

			// Duration buckets are keyed by whole seconds to keep percent-based tolerance intact.
			const int bucketSizeSeconds = 1;
			// Avoid bucket overhead for small datasets; fall back to the linear path.
			const int bucketActivationThreshold = 5000;
			var imageEntries = new List<FileEntry>();
			var videoEntries = new List<FileEntry>();
			var videoBuckets = new Dictionary<int, List<FileEntry>>();
			const int largeBucketThreshold = 400;

			for (int i = 0; i < ScanList.Count; i++) {
				var entry = ScanList[i];
				if (entry.IsImage) {
					imageEntries.Add(entry);
					continue;
				}
				videoEntries.Add(entry);
				// Bucket by duration seconds for candidate reduction in the large-data path.
				int bucketKey = (int)Math.Floor(entry.mediaInfo!.Duration.TotalSeconds / bucketSizeSeconds);
				if (!videoBuckets.TryGetValue(bucketKey, out var bucket)) {
					bucket = new List<FileEntry>();
					videoBuckets.Add(bucketKey, bucket);
				}
				bucket.Add(entry);
			}

			void MergeDuplicate(FileEntry entry, FileEntry compItem, float difference, DuplicateFlags flags) {
				lock (duplicateDict) {
					bool foundBase = duplicateDict.TryGetValue(entry.Path, out DuplicateItem? existingBase);
					bool foundComp = duplicateDict.TryGetValue(compItem.Path, out DuplicateItem? existingComp);

					if (foundBase && foundComp) {
						//this happens with 4+ identical items:
						//first, 2+ duplicate groups are found independently, they are merged in this branch
						if (existingBase!.GroupId != existingComp!.GroupId) {
							// Before merging two groups, verify that the representative
							// of each group is similar to the other group's representative.
							// This prevents daisy-chain merging where a single bridging
							// pair pulls two unrelated groups together.
							if (groupRepresentatives.TryGetValue(existingBase.GroupId, out var repBase) &&
								groupRepresentatives.TryGetValue(existingComp.GroupId, out var repComp) &&
								!CheckIfDuplicate(repBase, null, null, repComp, out _)) {
								mergesBlocked++;
								return; // Representatives aren't similar — don't merge.
							}
							Guid groupID = existingComp!.GroupId;
							List<DuplicateItem> baseMembers = groupMembers[existingBase.GroupId];
							foreach (DuplicateItem dup in groupMembers[groupID]) {
								dup.GroupId = existingBase.GroupId;
								baseMembers.Add(dup);
							}
							groupMembers.Remove(groupID);
							// Keep the representative of the absorbing group; remove the merged one.
							groupRepresentatives.Remove(groupID);
						}
					}
					else if (foundBase) {
						// New item joining an existing group — verify it matches the representative.
						if (groupRepresentatives.TryGetValue(existingBase!.GroupId, out var rep) &&
							!CheckIfDuplicate(rep, null, null, compItem, out _)) {
							mergesBlocked++;
							return;
						}
						var newItem = new DuplicateItem(compItem, difference, existingBase!.GroupId, flags);
						if (duplicateDict.TryAdd(compItem.Path, newItem))
							groupMembers[existingBase.GroupId].Add(newItem);
					}
					else if (foundComp) {
						// New item joining an existing group — verify it matches the representative.
						if (groupRepresentatives.TryGetValue(existingComp!.GroupId, out var rep) &&
							!CheckIfDuplicate(rep, null, null, entry, out _)) {
							mergesBlocked++;
							return;
						}
						var newItem = new DuplicateItem(entry, difference, existingComp!.GroupId, flags);
						if (duplicateDict.TryAdd(entry.Path, newItem))
							groupMembers[existingComp.GroupId].Add(newItem);
					}
					else {
						var groupId = Guid.NewGuid();
						var compDup = new DuplicateItem(compItem, difference, groupId, flags);
						var entryDup = new DuplicateItem(entry, difference, groupId, DuplicateFlags.None);
						duplicateDict.TryAdd(compItem.Path, compDup);
						duplicateDict.TryAdd(entry.Path, entryDup);
						groupMembers[groupId] = new List<DuplicateItem> { compDup, entryDup };
						groupRepresentatives[groupId] = entry;
					}
				}
			}

			bool TryCheckDuplicate(FileEntry entry, FileEntry compItem, byte[]?[]? flippedGrayBytes, ulong? flippedPHash, out float difference, out DuplicateFlags flags) {
				flags = DuplicateFlags.None;
				difference = 0;
				bool isDuplicate = CheckIfDuplicate(entry, null, null, compItem, out difference);
				if (Settings.CompareHorizontallyFlipped &&
					CheckIfDuplicate(entry, flippedGrayBytes, flippedPHash, compItem, out float flippedDifference)) {
					if (!isDuplicate || flippedDifference < difference) {
						flags |= DuplicateFlags.Flipped;
						isDuplicate = true;
						difference = flippedDifference;
					}
				}
				return isDuplicate;
			}

			double GetDurationToleranceSeconds(double durationSeconds) =>
				Settings.GetDurationToleranceSeconds(durationSeconds);

			// Compare one entry against candidate buckets (bucketed path).
			void CompareEntry(FileEntry entry, int entryIndex, IEnumerable<int> candidateBucketKeys) {
				pauseTokenSource.WaitWhilePaused(cancelationTokenSource.Token);

				float difference = 0;
				bool isDuplicate;
				DuplicateFlags flags;
				byte[]?[]? flippedGrayBytes = null;
				ulong? flippedPHash = null;
				double entryDurationSeconds = entry.mediaInfo!.Duration.TotalSeconds;
				double entryToleranceSeconds = GetDurationToleranceSeconds(entryDurationSeconds);

				if (Settings.CompareHorizontallyFlipped) {
					flippedGrayBytes = CreateFlippedGrayBytes(entry);
					if (usePHashing)
						flippedPHash = pHash.PerceptualHash.ComputePHashFromGray32x32(flippedGrayBytes[0]!);
				}

				foreach (int bucketKey in candidateBucketKeys) {
					if (!videoBuckets.TryGetValue(bucketKey, out var bucketEntries))
						continue;
					foreach (var compItem in bucketEntries) {
						int compIndex = compItem.compareIndex;
						if (compIndex <= entryIndex)
							continue;

						if (!entry.IsImage) {
							double compDurationSeconds = compItem.mediaInfo!.Duration.TotalSeconds;
							double compToleranceSeconds = GetDurationToleranceSeconds(compDurationSeconds);
							double allowedSeconds = Math.Min(entryToleranceSeconds, compToleranceSeconds);
							double diffSeconds = Math.Abs(entryDurationSeconds - compDurationSeconds);
							if (diffSeconds > allowedSeconds)
								continue;
						}

						if (Settings.FolderMatchMode == FolderMatchMode.SameFolderOnly &&
							!SameFolderAtDepth(entry.Folder, compItem.Folder, Settings.SameFolderDepth))
							continue;
						if (Settings.FolderMatchMode == FolderMatchMode.DifferentFolderOnly &&
							SameFolderAtDepth(entry.Folder, compItem.Folder, Settings.SameFolderDepth))
							continue;

						isDuplicate = TryCheckDuplicate(entry, compItem, flippedGrayBytes, flippedPHash, out difference, out flags);

						if (isDuplicate &&
							entry.FileSize == compItem.FileSize &&
							entry.mediaInfo!.Duration == compItem.mediaInfo!.Duration &&
							Settings.ExcludeHardLinks &&
							HardLinkUtils.AreSameFile(entry.Path, compItem.Path)) {
							isDuplicate = false;
						}

						if (isDuplicate)
							MergeDuplicate(entry, compItem, difference, flags);
					}
				}
				IncrementProgress(entry.Path);
			}

			// Images are always compared linearly; bucketing is only applied to videos.
			void CompareImages() {
				Action<int> compareAction = i => {
					var entry = imageEntries[i];
					byte[]?[]? flippedGrayBytes = null;
					if (Settings.CompareHorizontallyFlipped)
						flippedGrayBytes = CreateFlippedGrayBytes(entry);
					for (int n = i + 1; n < imageEntries.Count; n++) {
						var compItem = imageEntries[n];
						float difference = 0;
						DuplicateFlags flags;
						if (Settings.FolderMatchMode == FolderMatchMode.SameFolderOnly &&
							!SameFolderAtDepth(entry.Folder, compItem.Folder, Settings.SameFolderDepth))
							continue;
						if (Settings.FolderMatchMode == FolderMatchMode.DifferentFolderOnly &&
							SameFolderAtDepth(entry.Folder, compItem.Folder, Settings.SameFolderDepth))
							continue;
						// Images never take the pHash branch, so no flipped pHash is needed.
						bool isDuplicate = TryCheckDuplicate(entry, compItem, flippedGrayBytes, null, out difference, out flags);

						if (isDuplicate &&
							entry.FileSize == compItem.FileSize &&
							Settings.ExcludeHardLinks &&
							HardLinkUtils.AreSameFile(entry.Path, compItem.Path)) {
							isDuplicate = false;
						}

						if (isDuplicate)
							MergeDuplicate(entry, compItem, difference, flags);
					}
					IncrementProgress(entry.Path);
				};

				try {
					if (imageEntries.Count >= largeBucketThreshold) {
						Parallel.For(0, imageEntries.Count, new ParallelOptions { CancellationToken = cancelationTokenSource.Token, MaxDegreeOfParallelism = Settings.MaxDegreeOfParallelism }, compareAction);
					}
					else {
						for (int i = 0; i < imageEntries.Count; i++)
							compareAction(i);
					}
				}
				catch (OperationCanceledException) { }
			}

			// Linear compare path for small datasets to avoid bucket bookkeeping overhead.
			void CompareVideosLinear() {
				Action<int> compareAction = i => {
					pauseTokenSource.WaitWhilePaused(cancelationTokenSource.Token);

					var entry = videoEntries[i];
					float difference = 0;
					DuplicateFlags flags;
					byte[]?[]? flippedGrayBytes = null;
					ulong? flippedPHash = null;
					double entryDurationSeconds = entry.mediaInfo!.Duration.TotalSeconds;
					double entryToleranceSeconds = GetDurationToleranceSeconds(entryDurationSeconds);

					if (Settings.CompareHorizontallyFlipped) {
						flippedGrayBytes = CreateFlippedGrayBytes(entry);
						if (usePHashing)
							flippedPHash = pHash.PerceptualHash.ComputePHashFromGray32x32(flippedGrayBytes[0]!);
					}

					for (int n = i + 1; n < videoEntries.Count; n++) {
						var compItem = videoEntries[n];
						double compDurationSeconds = compItem.mediaInfo!.Duration.TotalSeconds;
						double compToleranceSeconds = GetDurationToleranceSeconds(compDurationSeconds);
						double allowedSeconds = Math.Min(entryToleranceSeconds, compToleranceSeconds);
						double diffSeconds = Math.Abs(entryDurationSeconds - compDurationSeconds);
						if (diffSeconds > allowedSeconds)
							continue;

						if (Settings.FolderMatchMode == FolderMatchMode.SameFolderOnly &&
							!SameFolderAtDepth(entry.Folder, compItem.Folder, Settings.SameFolderDepth))
							continue;
						if (Settings.FolderMatchMode == FolderMatchMode.DifferentFolderOnly &&
							SameFolderAtDepth(entry.Folder, compItem.Folder, Settings.SameFolderDepth))
							continue;

						bool isDuplicate = TryCheckDuplicate(entry, compItem, flippedGrayBytes, flippedPHash, out difference, out flags);
						if (isDuplicate &&
							entry.FileSize == compItem.FileSize &&
							entry.mediaInfo!.Duration == compItem.mediaInfo!.Duration &&
							Settings.ExcludeHardLinks &&
							HardLinkUtils.AreSameFile(entry.Path, compItem.Path)) {
							isDuplicate = false;
						}

						if (isDuplicate)
							MergeDuplicate(entry, compItem, difference, flags);
					}

					IncrementProgress(entry.Path);
				};

				try {
					if (videoEntries.Count >= largeBucketThreshold) {
						Parallel.For(0, videoEntries.Count, new ParallelOptions { CancellationToken = cancelationTokenSource.Token, MaxDegreeOfParallelism = Settings.MaxDegreeOfParallelism }, compareAction);
					}
					else {
						for (int i = 0; i < videoEntries.Count; i++)
							compareAction(i);
					}
				}
				catch (OperationCanceledException) { }
			}

			try {
				CompareImages();

				if (videoEntries.Count < bucketActivationThreshold) {
					// Small dataset: keep the simpler linear path.
					CompareVideosLinear();
				}
				else {
					// Large dataset: use buckets to reduce candidate comparisons.
					var smallBuckets = videoBuckets.Where(kvp => kvp.Value.Count < largeBucketThreshold).ToList();
					var largeBuckets = videoBuckets.Where(kvp => kvp.Value.Count >= largeBucketThreshold).ToList();

					Parallel.ForEach(smallBuckets, new ParallelOptions { CancellationToken = cancelationTokenSource.Token, MaxDegreeOfParallelism = Settings.MaxDegreeOfParallelism }, bucket => {
						foreach (var entry in bucket.Value) {
							int entryIndex = entry.compareIndex;
							double durationSeconds = entry.mediaInfo!.Duration.TotalSeconds;
							double maxDiffSeconds = GetDurationToleranceSeconds(durationSeconds);
							double minDuration = Math.Max(0d, durationSeconds - maxDiffSeconds);
							double maxDuration = durationSeconds + maxDiffSeconds;
							int minKey = (int)Math.Floor(minDuration / bucketSizeSeconds);
							int maxKey = (int)Math.Floor(maxDuration / bucketSizeSeconds);
							CompareEntry(entry, entryIndex, Enumerable.Range(minKey, maxKey - minKey + 1));
						}
					});

					foreach (var bucket in largeBuckets) {
						Parallel.For(0, bucket.Value.Count, new ParallelOptions { CancellationToken = cancelationTokenSource.Token, MaxDegreeOfParallelism = Settings.MaxDegreeOfParallelism }, i => {
							var entry = bucket.Value[i];
							int entryIndex = entry.compareIndex;
							double durationSeconds = entry.mediaInfo!.Duration.TotalSeconds;
							double maxDiffSeconds = GetDurationToleranceSeconds(durationSeconds);
							double minDuration = Math.Max(0d, durationSeconds - maxDiffSeconds);
							double maxDuration = durationSeconds + maxDiffSeconds;
							int minKey = (int)Math.Floor(minDuration / bucketSizeSeconds);
							int maxKey = (int)Math.Floor(maxDuration / bucketSizeSeconds);
							CompareEntry(entry, entryIndex, Enumerable.Range(minKey, maxKey - minKey + 1));
						});
					}
				}
			}
			catch (OperationCanceledException) { }
			if (mergesBlocked > 0)
				Logger.Instance.Info($"Group merge validation: blocked {mergesBlocked} merge(s) where group representatives were not similar");
			if (missingPHashFiles.Count > 0)
				Logger.Instance.Info($"pHash comparison: {missingPHashFiles.Count} file(s) had missing pHash data and were skipped in pHash comparisons. Delete the database (or rescan with 'Always retry failed sampling') to recompute.");
			Duplicates = new HashSet<DuplicateItem>(duplicateDict.Values);
			SplitDaisyChainGroups();

			// Release the transient snapshots; the gray-byte arrays themselves remain
			// owned by entry.grayBytes, only the alignment wrappers are dropped.
			foreach (FileEntry entry in ScanList) {
				entry.compareGray = null;
				entry.comparePHash = null;
			}
		}


		/// <summary>
		/// Phase 2 comparison: find pairs where a shorter video is a partial clip of a longer one,
		/// using audio fingerprint sliding-window matching.  Results are added to Duplicates.
		/// The comparison loop runs in parallel; grouping is applied sequentially afterward.
		/// </summary>
		void ScanForPartialDuplicates() {
			Logger.Instance.Info("Partial clip detection: building fingerprint index...");

			// Build a quick lookup for paths already covered by visual duplicate groups.
			var alreadyGrouped = new HashSet<string>(
				Duplicates.Select(d => d.Path),
				CoreUtils.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

			// Collect eligible videos: not an image, has a usable fingerprint, not already grouped.
			// Exclude silent/all-zero fingerprints: they Hamming-match any other silent track
			// at 100% and produce meaningless partial-clip groups. Older scan databases written
			// before this check may still contain all-zero fingerprints, so filter at read time.
			var videos = DatabaseUtils.Database
				.Where(e => !e.invalid && !e.IsImage &&
						!e.Flags.Has(EntryFlags.SilentAudioTrack) &&
						e.AudioFingerprint != null && e.AudioFingerprint.Length >= 2 &&
						!IsSilentFingerprint(e.AudioFingerprint) &&
						!alreadyGrouped.Contains(e.Path))
				.OrderByDescending(e => e.mediaInfo?.Duration ?? TimeSpan.Zero)
				.ToList();

			if (videos.Count < 2) {
				Logger.Instance.Info("Partial clip detection: fewer than 2 eligible videos, skipping.");
				return;
			}

			Logger.Instance.Info($"Partial clip detection: comparing {videos.Count} video(s) (fingerprint blocks: min={videos.Min(e => e.AudioFingerprint!.Length)}, max={videos.Max(e => e.AudioFingerprint!.Length)})...");

			float simThreshold = (float)Settings.PartialClipSimilarityThreshold;
			currentStageLabel = T("Scan.Stage.PartialCompare");
			InitProgress(videos.Count - 1);

			// --- Parallel phase: compute all matches without mutating shared state ---
			var matches = new ConcurrentBag<(int sourceIdx, int clipIdx, float sim, int offsetSec)>();
			long pairsChecked = 0;

			if (Settings.AudioCompareMethod == AudioCompareMethod.InvertedIndex)
				pairsChecked = BuildPartialClipMatchesIndexed(videos, simThreshold, matches);
			else Parallel.For(0, videos.Count - 1,
				new ParallelOptions {
					CancellationToken = cancelationTokenSource.Token,
					MaxDegreeOfParallelism = ParallelDegree
				},
				i => {
					FileEntry source = videos[i];
					IncrementProgress(Path.GetFileName(source.Path));
					double sourceSec = (source.mediaInfo?.Duration ?? TimeSpan.Zero).TotalSeconds;
					if (sourceSec < 1.0) return;

					for (int j = i + 1; j < videos.Count; j++) {
						if (cancelationTokenSource.IsCancellationRequested) break;
						FileEntry clip = videos[j];
						double clipSec = (clip.mediaInfo?.Duration ?? TimeSpan.Zero).TotalSeconds;
						if (clipSec < 1.0) continue;

						// Pre-filter 1: clip must be at least PartialClipMinRatio of source
						if (clipSec / sourceSec < Settings.PartialClipMinRatio) continue;

						// Pre-filter 2: clip must be shorter than 95% of source (visual dup handles the rest)
						if (clipSec / sourceSec >= 0.95) continue;

						// Fingerprint block sanity (each block ≈ 1 second)
						uint[] fpSource = source.AudioFingerprint!;
						uint[] fpClip = clip.AudioFingerprint!;
						if (fpClip.Length >= fpSource.Length) continue;

						Interlocked.Increment(ref pairsChecked);
						var (sim, offsetSec) = SlidingWindowCompare(fpClip, fpSource, simThreshold);

						if (sim >= simThreshold)
							matches.Add((i, j, sim, offsetSec));
					}
				});

			// --- Sequential phase: build groups from matches (preserving longest-source-first order) ---
			// A clip is kept with its first (longest) matching source. Sources whose only
			// candidate clips are already claimed are skipped entirely - adding them would
			// produce singleton groups in the result list.
			var assignments = AssignPartialClipGroups(matches);

			// Optional visual gate: drop pairs that match audio but differ visually at the
			// matched offset (e.g. videos sharing a backing track but otherwise unrelated).
			// Uses pHash when Settings.UsePHashing is on, else 32x32 grayscale percentage diff.
			if (Settings.PartialClipRequireVisualMatch && assignments.Count > 0) {
				int beforeCount = assignments.Count;
				int dropped = 0;
				var verified = new ConcurrentBag<(int, int, float, int, Guid)>();
				try {
					Parallel.ForEach(assignments, new ParallelOptions {
						CancellationToken = cancelationTokenSource.Token,
						MaxDegreeOfParallelism = ParallelDegree
					}, a => {
						bool pass = VerifyPartialClipVisually(videos[a.sourceIdx], videos[a.clipIdx], a.offsetSec, out float visualSim);
						if (pass) {
							verified.Add(a);
						}
						else {
							Interlocked.Increment(ref dropped);
							if (Settings.ExtendedFFToolsLogging)
								Logger.Instance.Info($"[Partial] Visual gate dropped {System.IO.Path.GetFileName(videos[a.clipIdx].Path)} in {System.IO.Path.GetFileName(videos[a.sourceIdx].Path)}: visualSim={visualSim:P1} (threshold {Settings.PartialClipVisualThreshold:P0})");
						}
					});
				}
				catch (OperationCanceledException) { }
				assignments = verified.OrderBy(a => a.Item1).ThenBy(a => a.Item2).ToList();
				Logger.Instance.Info($"Partial clip detection: visual gate kept {assignments.Count}/{beforeCount} assignment(s), dropped {dropped}");
			}

			var addedSources = new HashSet<int>();

			foreach (var (si, ci, sim, offsetSec, groupId) in assignments) {
				FileEntry source = videos[si];
				FileEntry clip = videos[ci];

				if (Settings.ExtendedFFToolsLogging)
					Logger.Instance.Info($"[Partial] {System.IO.Path.GetFileName(clip.Path)} in {System.IO.Path.GetFileName(source.Path)}: sim={sim:P1} @ {offsetSec}s (threshold {Settings.PartialClipSimilarityThreshold:P0}, fp {clip.AudioFingerprint!.Length}/{source.AudioFingerprint!.Length} blocks)");

				if (addedSources.Add(si))
					Duplicates.Add(new DuplicateItem(source, 0f, groupId, DuplicateFlags.None));

				Duplicates.Add(new DuplicateItem(clip, 1f - sim, groupId, DuplicateFlags.PartialClip) {
					PartialClipOffset = TimeSpan.FromSeconds(offsetSec)
				});
			}

			Logger.Instance.Info($"Partial clip detection: checked {pairsChecked} pair(s), found {matches.Count} candidate match(es), formed {assignments.Count} clip-source assignment(s).");
		}

		/// <summary>
		/// InvertedIndex partial-clip matcher (Settings.AudioCompareMethod). Builds an acoustid-style
		/// block→postings index — EXCLUDING value-0 silence blocks (they match any silent track and
		/// blow the index up) and blocks common to &gt;2% of videos (non-discriminative) — then for each
		/// clip votes candidate (source, offset) pairs from shared blocks and runs the exact Hamming
		/// compare only on strong peaks. Emits the same (sourceIdx, clipIdx, sim, offsetBlocks) matches
		/// as the brute path so the downstream grouping/visual-gate is untouched. Validated 2026-07-07
		/// on 18,874 real fingerprints: 100% recall of real content matches vs brute, ~40-108× faster
		/// (it does not emit the pure-silence "matches" brute generates — the visual gate drops those).
		/// Returns the number of candidate offsets verified (for the "checked N pairs" log).
		/// </summary>
		long BuildPartialClipMatchesIndexed(List<FileEntry> videos, float simThreshold,
			ConcurrentBag<(int sourceIdx, int clipIdx, float sim, int offsetSec)> matches) {
			const int voteThreshold = 3;                       // ≥3 shared blocks at one offset = candidate
			int nv = videos.Count;
			int dfThreshold = Math.Max(50, nv / 50);           // stopword when a block appears in >2% of videos

			// Document frequency (distinct videos per non-zero block), for the stopword filter.
			var df = new Dictionary<uint, int>();
			for (int v = 0; v < nv; v++) {
				var fp = videos[v].AudioFingerprint!;
				var seen = new HashSet<uint>();
				foreach (uint b in fp)
					if (b != 0 && seen.Add(b)) { df.TryGetValue(b, out int d); df[b] = d + 1; }
			}
			// Inverted index: block value -> (videoIdx, position). Skip silence and common blocks.
			var index = new Dictionary<uint, List<(int v, int p)>>();
			for (int v = 0; v < nv; v++) {
				var fp = videos[v].AudioFingerprint!;
				for (int p = 0; p < fp.Length; p++) {
					uint b = fp[p];
					if (b == 0) continue;
					if (df.TryGetValue(b, out int d) && d > dfThreshold) continue;
					if (!index.TryGetValue(b, out var lst)) index[b] = lst = new List<(int, int)>();
					lst.Add((v, p));
				}
			}

			long verified = 0;
			Parallel.For(0, nv, new ParallelOptions {
				CancellationToken = cancelationTokenSource.Token,
				MaxDegreeOfParallelism = ParallelDegree
			}, j => {
				IncrementProgress(Path.GetFileName(videos[j].Path));
				if (cancelationTokenSource.IsCancellationRequested)
					return;
				FileEntry clip = videos[j];
				double clipSec = (clip.mediaInfo?.Duration ?? TimeSpan.Zero).TotalSeconds;
				if (clipSec < 1.0)
					return;
				uint[] fpClip = clip.AudioFingerprint!;

				// Vote: a shared block at clip pos p and source pos sp implies alignment offset sp-p.
				var votes = new Dictionary<long, int>();
				for (int p = 0; p < fpClip.Length; p++) {
					uint b = fpClip[p];
					if (b == 0) continue;
					if (!index.TryGetValue(b, out var postings)) continue;
					foreach (var (v, sp) in postings) {
						if (v == j) continue;
						FileEntry src = videos[v];
						double sourceSec = (src.mediaInfo?.Duration ?? TimeSpan.Zero).TotalSeconds;
						if (sourceSec <= clipSec) continue;                          // source must be longer
						double r = clipSec / sourceSec;
						if (r < Settings.PartialClipMinRatio || r >= 0.95) continue; // same gates as brute
						if (fpClip.Length >= src.AudioFingerprint!.Length) continue;
						int off = sp - p;
						if (off < 0 || off > src.AudioFingerprint!.Length - fpClip.Length) continue;
						long key = ((long)v << 24) | (uint)off;
						votes.TryGetValue(key, out int c); votes[key] = c + 1;
					}
				}

				// Verify strong peaks with the exact Hamming compare (± a couple blocks for jitter).
				var matchedSources = new HashSet<int>();
				long localVerified = 0;
				foreach (var kv in votes) {
					if (kv.Value < voteThreshold) continue;
					int v = (int)(kv.Key >> 24);
					if (matchedSources.Contains(v)) continue;
					uint[] fpSource = videos[v].AudioFingerprint!;
					int off0 = (int)(kv.Key & 0xFFFFFF);
					int lo = Math.Max(0, off0 - 2), hi = Math.Min(fpSource.Length - fpClip.Length, off0 + 2);
					float best = 0f; int bestOff = off0;
					for (int o = lo; o <= hi; o++) {
						localVerified++;
						int bits = HammingDistance(fpClip, fpSource, o, fpClip.Length, int.MaxValue);
						float s = 1f - (float)bits / (fpClip.Length * 32);
						if (s > best) { best = s; bestOff = o; }
					}
					if (best >= simThreshold && matchedSources.Add(v))
						matches.Add((v, j, best, bestOff));       // source=v (longer), clip=j
				}
				Interlocked.Add(ref verified, localVerified);
			});
			return verified;
		}

		/// <summary>
		/// On-demand visual check for a partial-clip candidate. Decodes 1-3 frames from the
		/// clip and the source at the matched audio offset and compares them. Returns true
		/// when the average similarity meets <see cref="Settings.PartialClipVisualThreshold"/>,
		/// or when no frames could be sampled (in which case audio alone decides). Uses pHash
		/// when <see cref="Settings.UsePHashing"/> is enabled, otherwise grayscale percent diff.
		/// </summary>
		bool VerifyPartialClipVisually(FileEntry source, FileEntry clip, int offsetSec, out float visualSim) {
			visualSim = 0f;
			double sourceSec = (source.mediaInfo?.Duration ?? TimeSpan.Zero).TotalSeconds;
			double clipSec = (clip.mediaInfo?.Duration ?? TimeSpan.Zero).TotalSeconds;
			if (sourceSec <= 0 || clipSec <= 0) return true;

			// Sample times in clip-local seconds. Avoid the very edges so intros/outros
			// (often black or text-only) don't dominate the result.
			var clipTimes = new List<double>(3);
			if (clipSec >= 9.0) {
				clipTimes.Add(clipSec * 0.25);
				clipTimes.Add(clipSec * 0.50);
				clipTimes.Add(clipSec * 0.75);
			}
			else if (clipSec >= 3.0) {
				clipTimes.Add(clipSec * 0.33);
				clipTimes.Add(clipSec * 0.66);
			}
			else {
				clipTimes.Add(clipSec * 0.5);
			}

			bool useP = Settings.UsePHashing;
			double threshold = Settings.PartialClipVisualThreshold;
			int comparisons = 0;
			float simSum = 0f;

			// Collect the usable sample times first so each file is decoded in a single
			// batched session instead of one decoder open per frame.
			var srcSampleTimes = new List<double>(clipTimes.Count);
			var clipSampleTimes = new List<double>(clipTimes.Count);
			foreach (double t in clipTimes) {
				double srcAt = offsetSec + t;
				if (srcAt >= sourceSec - 0.1 || t >= clipSec - 0.1) continue;
				srcSampleTimes.Add(srcAt);
				clipSampleTimes.Add(t);
			}
			if (srcSampleTimes.Count == 0) return true;

			byte[]?[] srcFrames = FfmpegEngine.GetGrayFrames(source.Path, srcSampleTimes, Settings.ExtendedFFToolsLogging);
			byte[]?[] clipFrames = FfmpegEngine.GetGrayFrames(clip.Path, clipSampleTimes, Settings.ExtendedFFToolsLogging);

			for (int i = 0; i < srcSampleTimes.Count; i++) {
				byte[]? srcFrame = srcFrames[i];
				byte[]? clipFrame = clipFrames[i];
				if (srcFrame == null || clipFrame == null) continue;

				float pairSim;
				if (useP) {
					ulong hSrc = pHash.PerceptualHash.ComputePHashFromGray32x32(srcFrame);
					ulong hClip = pHash.PerceptualHash.ComputePHashFromGray32x32(clipFrame);
					pHash.PHashCompare.IsDuplicateByPercent(hSrc, hClip, out pairSim, threshold, strict: true);
				}
				else {
					float diff = GrayBytesUtils.PercentageDifference(srcFrame, clipFrame);
					pairSim = 1f - diff;
				}
				simSum += pairSim;
				comparisons++;
			}

			if (comparisons == 0) return true;
			visualSim = simSum / comparisons;
			return visualSim >= threshold;
		}

		/// <summary>
		/// Resolves overlapping partial-clip matches into deterministic group assignments.
		/// Matches are processed in (sourceIdx ASC, clipIdx ASC) order - since callers sort
		/// videos by duration descending, this means each clip is bound to the longest
		/// source that contains it. Subsequent matches for an already-assigned clip are
		/// dropped, and their would-be source is omitted unless it has unclaimed clips of
		/// its own. This prevents singleton groups in the output.
		/// </summary>
		internal static List<(int sourceIdx, int clipIdx, float sim, int offsetSec, Guid groupId)>
			AssignPartialClipGroups(IEnumerable<(int sourceIdx, int clipIdx, float sim, int offsetSec)> matches) {
			var sourceGroupId = new Dictionary<int, Guid>();
			var assignedClips = new HashSet<int>();
			var assignments = new List<(int, int, float, int, Guid)>();

			foreach (var (si, ci, sim, offsetSec) in matches.OrderBy(m => m.sourceIdx).ThenBy(m => m.clipIdx)) {
				if (!assignedClips.Add(ci)) continue;

				if (!sourceGroupId.TryGetValue(si, out Guid groupId)) {
					groupId = Guid.NewGuid();
					sourceGroupId[si] = groupId;
				}
				assignments.Add((si, ci, sim, offsetSec, groupId));
			}
			return assignments;
		}

		/// <summary>
		/// Slides <paramref name="shorter"/> over <paramref name="longer"/> and returns the
		/// best average Hamming similarity (0–1) and the offset (in seconds / blocks) at which
		/// it occurs.  Uses SIMD-accelerated XOR where available and skips offsets early when
		/// the accumulated Hamming distance already exceeds what could beat the current best.
		/// </summary>
		/// <param name="minSim">Minimum similarity the caller cares about (e.g. the user threshold).
		/// Offsets that cannot reach this value are skipped via early exit.</param>
		internal static (float similarity, int offsetBlocks) SlidingWindowCompare(uint[] shorter, uint[] longer, float minSim = 0f) {
			int lenS = shorter.Length;
			int lenL = longer.Length;
			int maxOffset = lenL - lenS;
			int totalBitsCapacity = lenS * 32;

			float bestSim = 0f;
			int bestOffset = 0;

			for (int offset = 0; offset <= maxOffset; offset++) {
				// The maximum number of differing bits we can tolerate and still
				// beat the current best (or the caller's minimum threshold).
				int maxAllowedBits = (int)((1f - Math.Max(bestSim, minSim)) * totalBitsCapacity);

				int totalBits = HammingDistance(shorter, longer, offset, lenS, maxAllowedBits);

				if (totalBits > maxAllowedBits)
					continue; // early exit triggered inside HammingDistance

				float sim = 1f - (float)totalBits / totalBitsCapacity;
				if (sim > bestSim) {
					bestSim = sim;
					bestOffset = offset;
				}
			}

			return (bestSim, bestOffset);
		}

		/// <summary>
		/// Computes the Hamming distance (total differing bits) between
		/// <paramref name="a"/>[0..len) and <paramref name="b"/>[offset..offset+len).
		/// Uses 256-bit or 128-bit SIMD for the XOR when hardware support is available.
		/// Returns early (with a value &gt; <paramref name="maxAllowedBits"/>) when the
		/// running total exceeds the budget, avoiding unnecessary work on non-matching offsets.
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static int HammingDistance(uint[] a, uint[] b, int offset, int len, int maxAllowedBits) {
			int totalBits = 0;
			int k = 0;

			// --- Vector256 path (8 × uint per iteration) ---
			if (Vector256.IsHardwareAccelerated && len >= 8) {
				ref uint aRef = ref MemoryMarshal.GetArrayDataReference(a);
				ref uint bRef = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(b), offset);

				for (; k + 8 <= len; k += 8) {
					var va = Vector256.LoadUnsafe(ref aRef, (nuint)k);
					var vb = Vector256.LoadUnsafe(ref bRef, (nuint)k);
					// Popcount over 64-bit lanes: half the PopCount calls of per-uint
					// counting. (Vector256.PopCount still has no hardware path here.)
					var xored = (va ^ vb).AsUInt64();

					totalBits += BitOperations.PopCount(xored.GetElement(0))
							   + BitOperations.PopCount(xored.GetElement(1))
							   + BitOperations.PopCount(xored.GetElement(2))
							   + BitOperations.PopCount(xored.GetElement(3));

					if (totalBits > maxAllowedBits) return totalBits;
				}
			}
			// --- Vector128 path (4 × uint per iteration, e.g. ARM NEON) ---
			else if (Vector128.IsHardwareAccelerated && len >= 4) {
				ref uint aRef = ref MemoryMarshal.GetArrayDataReference(a);
				ref uint bRef = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(b), offset);

				for (; k + 4 <= len; k += 4) {
					var va = Vector128.LoadUnsafe(ref aRef, (nuint)k);
					var vb = Vector128.LoadUnsafe(ref bRef, (nuint)k);
					var xored = (va ^ vb).AsUInt64();

					totalBits += BitOperations.PopCount(xored.GetElement(0))
							   + BitOperations.PopCount(xored.GetElement(1));

					if (totalBits > maxAllowedBits) return totalBits;
				}
			}

			// --- Scalar remainder ---
			for (; k < len; k++) {
				totalBits += BitOperations.PopCount(a[k] ^ b[offset + k]);
			}

			return totalBits;
		}

		/// <summary>
		/// Post-processes duplicate groups to break apart "daisy chains" where transitive
		/// merging created groups containing items that aren't actually similar to each other.
		/// For each group with 3+ members, builds a pairwise similarity graph, then
		/// iteratively prunes members that are similar to fewer than half the group.
		/// Pruned items are re-clustered into their own groups if they still have matches.
		/// </summary>
		void SplitDaisyChainGroups() {
			// Build a fast lookup from path -> FileEntry for re-comparing pairs.
			var dbLookup = new Dictionary<string, FileEntry>(
				CoreUtils.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
			foreach (FileEntry fe in DatabaseUtils.Database)
				dbLookup[fe.Path] = fe;

			// Group duplicates by GroupId; only process groups with 3+ members.
			var groups = Duplicates
				.GroupBy(d => d.GroupId)
				.Where(g => g.Count() >= 3)
				.ToList();

			if (groups.Count == 0) return;

			int groupsSplit = 0;
			int itemsRemoved = 0;

			foreach (var group in groups) {
				var members = group.ToList();
				int n = members.Count;

				// Resolve FileEntry for each member; skip group if any entry is missing
				// or lacks a compare snapshot (defensive — all visual duplicates stem
				// from the snapshot-validated scan list).
				var entries = new FileEntry[n];
				bool allFound = true;
				for (int i = 0; i < n; i++) {
					if (!dbLookup.TryGetValue(members[i].Path, out var fe) || fe.compareGray == null) {
						allFound = false;
						break;
					}
					entries[i] = fe;
				}
				if (!allFound) continue;

				// Build pairwise similarity matrix.
				var similar = new bool[n, n];
				for (int i = 0; i < n; i++) {
					similar[i, i] = true;
					for (int j = i + 1; j < n; j++) {
						bool isSimilar = CheckIfDuplicate(entries[i], null, null, entries[j], out _);
						similar[i, j] = isSimilar;
						similar[j, i] = isSimilar;
					}
				}

				// Iterative pruning: remove the least-connected member until every
				// remaining member is similar to at least half of the other members.
				var active = new List<int>(Enumerable.Range(0, n));
				var pruned = new List<int>();

				bool changed = true;
				while (changed && active.Count >= 2) {
					changed = false;
					int worstIdx = -1;
					int worstConnections = int.MaxValue;

					for (int ai = 0; ai < active.Count; ai++) {
						int idx = active[ai];
						int connections = 0;
						for (int aj = 0; aj < active.Count; aj++) {
							if (ai != aj && similar[idx, active[aj]])
								connections++;
						}
						if (connections < worstConnections) {
							worstConnections = connections;
							worstIdx = ai;
						}
					}

					// Prune if the least-connected member is similar to fewer than half.
					int requiredConnections = (active.Count - 1 + 1) / 2; // ceiling of (count-1)/2
					if (worstConnections < requiredConnections) {
						pruned.Add(active[worstIdx]);
						active.RemoveAt(worstIdx);
						changed = true;
					}
				}

				if (pruned.Count == 0) continue;

				groupsSplit++;

				// Assign a new GroupId to the surviving core group (if 2+ members remain).
				if (active.Count >= 2) {
					var coreGroupId = Guid.NewGuid();
					foreach (int idx in active)
						members[idx].GroupId = coreGroupId;
				}
				else {
					// Core collapsed to a single item — remove it too.
					foreach (int idx in active) {
						Duplicates.Remove(members[idx]);
						itemsRemoved++;
					}
					active.Clear();
				}

				// Re-cluster pruned items among themselves: form groups from connected
				// components using the same similarity matrix.
				var visited = new HashSet<int>();
				foreach (int seed in pruned) {
					if (visited.Contains(seed)) continue;
					var component = new List<int>();
					var queue = new Queue<int>();
					queue.Enqueue(seed);
					visited.Add(seed);
					while (queue.Count > 0) {
						int cur = queue.Dequeue();
						component.Add(cur);
						foreach (int other in pruned) {
							if (!visited.Contains(other) && similar[cur, other]) {
								visited.Add(other);
								queue.Enqueue(other);
							}
						}
					}

					if (component.Count >= 2) {
						// Recursively validate this sub-group too: apply the same
						// majority-pruning before accepting it.
						var subActive = new List<int>(component);
						bool subChanged = true;
						while (subChanged && subActive.Count >= 2) {
							subChanged = false;
							int subWorstIdx = -1;
							int subWorstConn = int.MaxValue;
							for (int ai = 0; ai < subActive.Count; ai++) {
								int idx = subActive[ai];
								int conn = 0;
								for (int aj = 0; aj < subActive.Count; aj++) {
									if (ai != aj && similar[idx, subActive[aj]])
										conn++;
								}
								if (conn < subWorstConn) {
									subWorstConn = conn;
									subWorstIdx = ai;
								}
							}
							int subRequired = (subActive.Count - 1 + 1) / 2;
							if (subWorstConn < subRequired) {
								// Remove this item entirely — it doesn't fit anywhere.
								Duplicates.Remove(members[subActive[subWorstIdx]]);
								itemsRemoved++;
								subActive.RemoveAt(subWorstIdx);
								subChanged = true;
							}
						}

						if (subActive.Count >= 2) {
							var subGroupId = Guid.NewGuid();
							foreach (int idx in subActive)
								members[idx].GroupId = subGroupId;
						}
						else {
							foreach (int idx in subActive) {
								Duplicates.Remove(members[idx]);
								itemsRemoved++;
							}
						}
					}
					else {
						// Single pruned item with no matches among other pruned items.
						Duplicates.Remove(members[component[0]]);
						itemsRemoved++;
					}
				}
			}

			if (groupsSplit > 0)
				Logger.Instance.Info($"Daisy-chain validation: split {groupsSplit} group(s), removed {itemsRemoved} singleton item(s)");
		}

		void LogGroupStatistics() {
			var groupSizes = Duplicates
				.GroupBy(d => d.GroupId)
				.Select(g => g.Count())
				.ToList();
			if (groupSizes.Count == 0) return;
			int totalItems = groupSizes.Sum();
			int maxSize = groupSizes.Max();
			double avgSize = groupSizes.Average();
			int groupsOver5 = groupSizes.Count(s => s > 5);
			int groupsOver10 = groupSizes.Count(s => s > 10);
			Logger.Instance.Info($"Group statistics: {groupSizes.Count} groups, {totalItems} items, " +
				$"avg size {avgSize:F1}, max size {maxSize}, " +
				$"groups with >5 items: {groupsOver5}, >10 items: {groupsOver10}");
		}

		public async void CleanupDatabase() {
			await Task.Run(() => {
				DatabaseUtils.CleanupDatabase();
			});
			DatabaseCleaned?.Invoke(this, new EventArgs());
		}
		public static void ClearDatabase() => DatabaseUtils.ClearDatabase();

		// A "ghost" is an entry whose file is gone from a currently-MOUNTED drive and that carries
		// no comparable data at all — no usable frame hash and no audio fingerprint. Unlike a
		// tombstone (missing file WITH fingerprints, deliberately kept so a re-download is
		// recognized — see TOMBSTONE-DESIGN.md), a ghost can never match anything and can never
		// heal (the file is gone), so it is pure dead weight iterated by every scan. Offline
		// drives are excluded, same discipline as PathIsTombstone: their files may still exist.
		static bool IsGhostEntry(FileEntry e, Dictionary<string, bool> driveReadyCache) {
			if (e.AudioFingerprint != null) return false;
			if (e.grayBytes != null)
				foreach (var v in e.grayBytes.Values)
					if (v != null) return false;   // at least one usable frame hash -> keep as tombstone
			if (File.Exists(e.Path)) return false;
			string root = DriveRootOf(e.Path);
			if (!driveReadyCache.TryGetValue(root, out bool ready))
				driveReadyCache[root] = ready = IsDriveReady(e.Path);
			return ready;
		}
		/// <summary>Counts what <see cref="PruneGhostEntries"/> would remove (read-only preview).</summary>
		public static int CountGhostEntries() {
			var readyCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
			int n = 0;
			foreach (var e in DatabaseUtils.Database)
				if (IsGhostEntry(e, readyCache)) n++;
			return n;
		}
		/// <summary>Removes ghost entries and saves the database. Do not call during a scan.</summary>
		public static int PruneGhostEntries() {
			var readyCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
			var ghosts = new List<FileEntry>();
			foreach (var e in DatabaseUtils.Database)
				if (IsGhostEntry(e, readyCache)) ghosts.Add(e);
			foreach (var g in ghosts)
				DatabaseUtils.Database.Remove(g);
			if (ghosts.Count > 0)
				DatabaseUtils.SaveDatabase();
			Logger.Instance.Info($"Pruned {ghosts.Count:N0} ghost entries (file missing on a mounted drive, no comparable fingerprint data).");
			return ghosts.Count;
		}

		/// <summary>
		/// Pure core of <see cref="PruneRelocatedOrphans"/>, split out so it is testable without a
		/// filesystem: given the OsHashes of files that still exist, a relocated orphan is any gone
		/// entry whose OsHash is among them — its exact content lives on at another (present) path.
		/// </summary>
		internal static List<FileEntry> SelectRelocatedOrphans(HashSet<string> liveOsHashes, IEnumerable<FileEntry> goneEntries) {
			var orphans = new List<FileEntry>();
			foreach (var g in goneEntries)
				if (g.OsHash != null && liveOsHashes.Contains(g.OsHash))
					orphans.Add(g);
			return orphans;
		}

		// A "relocated orphan" is an entry whose file is gone from a MOUNTED drive while its exact
		// content (same OsHash) still exists at another path. BuildFileList's relink missed the move —
		// the old entry had no OsHash yet when the new copy was first enumerated, so that copy was
		// analysed fresh instead of re-keyed — leaving a stale duplicate that inflates 'Missing' every
		// scan and can never heal. Removing it loses nothing: a tombstone flags a re-download of GONE
		// content, but this content is present (the live copy supersedes it, and inherits the tombstone
		// role itself if it is later deleted). Runs at the end of GatherInfos, once in-scope live
		// entries have had their OsHash backfilled. Offline drives are skipped (their files may exist).
		static int PruneRelocatedOrphans() {
			var readyCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
			bool Ready(string path) {
				string root = DriveRootOf(path);
				if (!readyCache.TryGetValue(root, out bool r))
					readyCache[root] = r = IsDriveReady(path);
				return r;
			}
			// One existence check per OsHash-bearing entry on a mounted drive: present -> its content
			// is a valid twin target; absent -> a candidate orphan.
			var liveOsHashes = new HashSet<string>(StringComparer.Ordinal);
			var gone = new List<FileEntry>();
			foreach (var e in DatabaseUtils.Database) {
				if (e.OsHash == null || !Ready(e.Path)) continue;
				if (File.Exists(e.Path)) liveOsHashes.Add(e.OsHash);
				else gone.Add(e);
			}
			if (liveOsHashes.Count == 0 || gone.Count == 0) return 0;
			var orphans = SelectRelocatedOrphans(liveOsHashes, gone);
			foreach (var o in orphans)
				DatabaseUtils.Database.Remove(o);
			if (orphans.Count > 0)
				Logger.Instance.Info($"Removed {orphans.Count:N0} relocated-orphan DB entr{(orphans.Count == 1 ? "y" : "ies")} " +
					"(file moved away, identical content already present elsewhere — relink missed it; the live copy supersedes it).");
			return orphans.Count;
		}

		// Opt-in (Settings.AutoDeleteUnrecoverableFiles): recycle videos that stayed unrecoverable after
		// the full retry budget and drop their DB entries. Runs at scan end, off the parallel workers.
		// Recycle bin only (recoverable); files gone/offline are skipped; the shell may leave some, so
		// only paths that actually disappeared are removed from the DB. Every deletion is logged.
		static int AutoDeleteUnrecoverableVideos(int maxSamplingAttempts) {
			var readyCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
			bool Ready(string path) {
				string root = DriveRootOf(path);
				if (!readyCache.TryGetValue(root, out bool r))
					readyCache[root] = r = IsDriveReady(path);
				return r;
			}
			var victims = new List<FileEntry>();
			foreach (var e in DatabaseUtils.Database)
				if (IsUnrecoverableVideo(e, maxSamplingAttempts) && Ready(e.Path) && File.Exists(e.Path))
					victims.Add(e);
			if (victims.Count == 0) return 0;

			var recycled = new HashSet<string>(
				FileUtils.RecycleFiles(victims.Select(v => v.Path).ToList()),
				CoreUtils.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
			int removed = 0;
			foreach (var v in victims)
				if (recycled.Contains(v.Path)) {
					DatabaseUtils.Database.Remove(v);
					Logger.Instance.Info($"Auto-deleted unrecoverable video to recycle bin (frame decode failed {v.SamplingFailCount}x): '{v.Path}'");
					removed++;
				}
			if (removed > 0)
				Logger.Instance.Info($"Auto-deleted {removed:N0} unrecoverable video(s) to the recycle bin.");
			return removed;
		}
		public static bool ExportDataBaseToJson(string jsonFile, JsonSerializerOptions options) => DatabaseUtils.ExportDatabaseToJson(jsonFile, options);
		public static bool ImportDataBaseFromJson(string jsonFile, JsonSerializerOptions options) => DatabaseUtils.ImportDatabaseFromJson(jsonFile, options);

		/// <summary>
		/// Extracts a single JPEG thumbnail from a video or image file on demand.
		/// Intended for web endpoints that need higher resolution than the default 100px scan thumbnails.
		/// </summary>
		/// <param name="filePath">Absolute path to the media file.</param>
		/// <param name="position">Seek position (ignored for images).</param>
		/// <param name="maxWidth">Target width in pixels. 0 = original resolution.</param>
		/// <returns>JPEG bytes, or null on failure.</returns>
		public static byte[]? ExtractThumbnailJpeg(string filePath, TimeSpan position, int maxWidth = 0, int jpegQuality = 0) {
			if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;

			bool isImage = IsImageExtension(Path.GetExtension(filePath));
			return FfmpegEngine.GetThumbnail(new FfmpegSettings {
				File = filePath,
				Position = isImage ? TimeSpan.Zero : position,
				GrayScale = 0,
				Fullsize = (byte)(maxWidth == 0 ? 1 : 0),
				MaxWidth = maxWidth,
				JpegQuality = jpegQuality,
				SoftwareDecodeOnly = isImage,
			}, false);
		}

		static bool IsImageExtension(string ext) =>
			ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
			ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
			ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
			ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
			ext.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
			ext.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
			ext.Equals(".tiff", StringComparison.OrdinalIgnoreCase) ||
			ext.Equals(".tif", StringComparison.OrdinalIgnoreCase);

		/// <summary>
		/// Whether an item should be (re)processed for thumbnails. Items with no thumbnails
		/// load on first pass; items whose sole image is the NoThumbnailImage placeholder
		/// represent a prior extraction failure and must remain eligible for explicit retry,
		/// otherwise a "Load thumbnails for group" click silently no-ops on the very items
		/// the user is trying to recover (issue #748).
		/// </summary>
		internal static bool ShouldRetryThumbnails(DuplicateItem item, byte[]? placeholder, int requiredWidth = 0) {
			if (item.ImageList == null || item.ImageList.Count == 0) return true;
			if (placeholder != null && item.ImageList.Count == 1 && ReferenceEquals(item.ImageList[0], placeholder)) return true;
			// Explicit reloads also refresh thumbnails extracted at a smaller width than
			// the current setting (issue #777). Width 0 = unknown (older backups) — those
			// stay as-is rather than forcing a re-extract of everything.
			if (requiredWidth > 0 && item.ThumbnailWidth > 0 && item.ThumbnailWidth < requiredWidth) return true;
			return false;
		}

		/// <summary>
		/// The frame sample positions are populated during scan setup. When results are restored
		/// from a saved backup without running a scan, the list is empty, so video thumbnail
		/// re-extraction would sample zero frames and yield placeholders only (issue #775).
		/// Rebuild it on demand from the configured thumbnail count.
		/// </summary>
		internal void EnsureThumbnailPositions() {
			if (positionList.Count > 0) return;
			float positionCounter = 0f;
			for (int i = 0; i < Settings.ThumbnailCount; i++) {
				positionCounter += 1.0F / (Settings.ThumbnailCount + 1);
				positionList.Add(positionCounter);
			}
		}

		public async Task RetrieveThumbnailsForItems(IEnumerable<DuplicateItem> items) {
			// Explicit reloads also refresh thumbnails whose extraction width is below the
			// current setting (issue #777); the automatic post-scan pass does not.
			int requiredWidth = Settings.ThumbnailMaxWidth > 0 ? Settings.ThumbnailMaxWidth : 100;
			var dupList = items.Where(d => ShouldRetryThumbnails(d, NoThumbnailImage, requiredWidth)).ToList();
			if (dupList.Count == 0) {
				Logger.Instance.Info("Explicit thumbnail retry: nothing to do (all selected items already have up-to-date thumbnails).");
				return;
			}
			EnsureThumbnailPositions();
			Logger.Instance.Info($"Explicit thumbnail retry: starting for {dupList.Count} item(s).");
			int loaded = 0, placeholders = 0, skippedMissing = 0;
			try {
				await Parallel.ForEachAsync(dupList, new ParallelOptions { MaxDegreeOfParallelism = Settings.MaxDegreeOfParallelism }, (entry, cancellationToken) => {
					List<byte[]>? list = null;
					bool needsThumbnails = !Settings.IncludeNonExistingFiles || File.Exists(entry.Path);
					List<TimeSpan>? timeStamps = null;
					int maxDim = Settings.ThumbnailMaxWidth > 0 ? Settings.ThumbnailMaxWidth : 100;

					if (!needsThumbnails) {
						Interlocked.Increment(ref skippedMissing);
					}
					else if (entry.IsImage) {
						timeStamps = new(0);
						list = new List<byte[]>(1);
						var b = ExtractThumbnailJpeg(entry.Path, TimeSpan.Zero, maxDim);
						if (b == null || b.Length == 0) {
							Logger.Instance.Info($"Failed loading image from file: '{entry.Path}'.");
							return ValueTask.CompletedTask;
						}
						list.Add(b);
						entry.ThumbnailWidth = maxDim;
						Interlocked.Increment(ref loaded);
					}
					else {
						list = new List<byte[]>(positionList.Count);
						timeStamps = new List<TimeSpan>(positionList.Count);
						int failedPositions = 0;
						for (int j = 0; j < positionList.Count; j++) {
							var timestamp = TimeSpan.FromSeconds(entry.Duration.TotalSeconds * positionList[j]);
							var b = FfmpegEngine.ExtractThumbnailJpeg(entry.Path, timestamp, maxDim, Settings.ExtendedFFToolsLogging);
							if (b == null || b.Length == 0) {
								failedPositions++;
								Logger.Instance.Info($"Failed extracting thumbnail at {timestamp} for '{entry.Path}', skipping that position.");
								continue;
							}
							list.Add(b);
							timeStamps.Add(timestamp);
						}
						if (list.Count == 0 && NoThumbnailImage != null) {
							list.Add(NoThumbnailImage);
							timeStamps.Add(TimeSpan.Zero);
							entry.ThumbnailWidth = 0;
							Logger.Instance.Info($"Using placeholder for '{entry.Path}' — all {positionList.Count} sample position(s) failed.");
							Interlocked.Increment(ref placeholders);
						}
						else if (list.Count > 0 && failedPositions > 0) {
							entry.ThumbnailWidth = maxDim;
							Logger.Instance.Info($"Loaded {list.Count}/{positionList.Count} thumbnail(s) for '{entry.Path}' ({failedPositions} position(s) failed).");
							Interlocked.Increment(ref loaded);
						}
						else if (list.Count > 0) {
							entry.ThumbnailWidth = maxDim;
							Interlocked.Increment(ref loaded);
						}
					}
					Debug.Assert(timeStamps != null);
					entry.SetThumbnails(list ?? (NoThumbnailImage != null ? new() { NoThumbnailImage } : new()), timeStamps!);

					return ValueTask.CompletedTask;
				});
			}
			catch (OperationCanceledException) { }
			Logger.Instance.Info($"Explicit thumbnail retry complete: {loaded} fully loaded, {placeholders} placeholder, {skippedMissing} skipped (missing on disk).");
		}
		public async void RetrieveThumbnails() {
			var dupList = Duplicates.Where(d => ShouldRetryThumbnails(d, NoThumbnailImage)).ToList();
			int total = dupList.Count;
			int done = 0;
			int lastNotified = 0;
			int loaded = 0, placeholders = 0, skippedMissing = 0;
			Logger.Instance.Info($"Thumbnail loading: starting for {total} item(s).");

			var totalSw = Stopwatch.StartNew();
			var sw = Stopwatch.StartNew();
			try {
				await Parallel.ForEachAsync(dupList, new ParallelOptions { CancellationToken = cancelationTokenSource.Token, MaxDegreeOfParallelism = Settings.MaxDegreeOfParallelism }, (entry, cancellationToken) => {
					List<byte[]>? list = null;
					bool needsThumbnails = !Settings.IncludeNonExistingFiles || File.Exists(entry.Path);
					List<TimeSpan>? timeStamps = null;

					int current = Interlocked.Increment(ref done);
					if (sw.ElapsedMilliseconds > 300)
						if (Interlocked.Exchange(ref lastNotified, current) < current) {
							sw.Restart(); // only this thread resets the stopwatch
							ThumbnailProgress?.Invoke(current, total);
						}

					int maxDim = Settings.ThumbnailMaxWidth > 0 ? Settings.ThumbnailMaxWidth : 100;

					if (!needsThumbnails) {
						Interlocked.Increment(ref skippedMissing);
					}
					else if (entry.IsImage) {
						//For images it doesn't make sense to load the actual image more than once
						timeStamps = new(0);
						list = new List<byte[]>(1);
						var b = ExtractThumbnailJpeg(entry.Path, TimeSpan.Zero, maxDim);
						if (b == null || b.Length == 0) {
							Logger.Instance.Info($"Failed loading image from file: '{entry.Path}'.");
							return ValueTask.CompletedTask;
						}
						list.Add(b);
						entry.ThumbnailWidth = maxDim;
						Interlocked.Increment(ref loaded);
					}
					else {
						list = new List<byte[]>(positionList.Count);
						timeStamps = new List<TimeSpan>(positionList.Count);
						int failedPositions = 0;
						for (int j = 0; j < positionList.Count; j++) {
							var timestamp = TimeSpan.FromSeconds(entry.Duration.TotalSeconds * positionList[j]);
							var b = FfmpegEngine.ExtractThumbnailJpeg(entry.Path, timestamp, maxDim, Settings.ExtendedFFToolsLogging);
							if (b == null || b.Length == 0) {
								failedPositions++;
								Logger.Instance.Info($"Failed extracting thumbnail at {timestamp} for '{entry.Path}', skipping that position.");
								continue;
							}
							list.Add(b);
							timeStamps.Add(timestamp);
						}
						if (list.Count == 0 && NoThumbnailImage != null) {
							list.Add(NoThumbnailImage);
							timeStamps.Add(TimeSpan.Zero);
							entry.ThumbnailWidth = 0;
							Logger.Instance.Info($"Using placeholder for '{entry.Path}' — all {positionList.Count} sample position(s) failed.");
							Interlocked.Increment(ref placeholders);
						}
						else if (list.Count > 0 && failedPositions > 0) {
							entry.ThumbnailWidth = maxDim;
							Logger.Instance.Info($"Loaded {list.Count}/{positionList.Count} thumbnail(s) for '{entry.Path}' ({failedPositions} position(s) failed).");
							Interlocked.Increment(ref loaded);
						}
						else if (list.Count > 0) {
							entry.ThumbnailWidth = maxDim;
							Interlocked.Increment(ref loaded);
						}
					}
					Debug.Assert(timeStamps != null);
					entry.SetThumbnails(list ?? (NoThumbnailImage != null ? new() { NoThumbnailImage } : new()), timeStamps!);

					return ValueTask.CompletedTask;
				});
			}
			catch (OperationCanceledException) { }
			Logger.Instance.Info($"Thumbnail loading complete: {loaded} fully loaded, {placeholders} placeholder, {skippedMissing} skipped (missing on disk) in {totalSw.Elapsed.TotalSeconds:F1}s.");
			ThumbnailsRetrieved?.Invoke(this, new EventArgs());
		}

		static bool GetGrayBytesFromImage(FileEntry imageFile, bool useExifIfAvailable, bool extendedLogging) {
			try {
				// Decode through FFmpeg — the same pipeline videos use — so image and video
				// gray bytes share identical grayscale conversion and scaling.
				byte[]? grayBytes;
				int width, height;
				if (!FfmpegEngine.TryGetImageInfoAndGrayBytes(imageFile.Path, out grayBytes, out width, out height, extendedLogging)) {
					// CLI fallback. Read dimensions straight from the file header first: some
					// PNGs trip FFprobe's demuxer with a bogus "chunk too big" error (#805),
					// and the header carries the dimensions without decoding. Only fall back
					// to FFprobe when the header reader doesn't recognise the format.
					if (!ImageHeader.TryGetDimensions(imageFile.Path, out width, out height)) {
						MediaInfo? info = FFProbeEngine.GetMediaInfo(imageFile.Path, extendedLogging);
						var stream = info?.Streams?.FirstOrDefault(s => s.Width > 0 && s.Height > 0);
						width = stream?.Width ?? 0;
						height = stream?.Height ?? 0;
					}
					grayBytes = FfmpegEngine.GetThumbnail(new FfmpegSettings {
						File = imageFile.Path,
						Position = TimeSpan.Zero,
						GrayScale = 1,
						SoftwareDecodeOnly = true,
					}, extendedLogging);
				}

				if (grayBytes == null) {
					imageFile.Flags.Set(EntryFlags.ThumbnailError);
					return false;
				}

				imageFile.mediaInfo = new MediaInfo {
					Streams = new[] {
							new MediaInfo.StreamInfo {Height = height, Width = width}
						}
				};

				// Extract EXIF capture date if enabled
				if (useExifIfAvailable) {
					if (ExifReader.TryGetDateTaken(imageFile.Path, out DateTime exifDate)) {
						imageFile.DateCreated = exifDate;
					}
					else {
						// HEIC/HEIF carry the date in the container instead; read it via FFprobe.
						string ext = Path.GetExtension(imageFile.Path);
						if (ext.Equals(".heic", StringComparison.OrdinalIgnoreCase) ||
							ext.Equals(".heif", StringComparison.OrdinalIgnoreCase)) {
							var creationTime = FFProbeEngine.GetCreationTime(imageFile.Path);
							if (creationTime.HasValue)
								imageFile.DateCreated = creationTime.Value;
						}
					}
				}

				if (!GrayBytesUtils.VerifyGrayScaleValues(grayBytes)) {
					imageFile.Flags.Set(EntryFlags.TooDark);
					Logger.Instance.Info($"ERROR: Graybytes too dark of: {imageFile.Path}");
					return false;
				}

				imageFile.grayBytes.Add(0, grayBytes);
				return true;
			}
			catch (Exception ex) {
				Logger.Instance.Info(
					$"Exception, file: {imageFile.Path}, reason: {ex.Message}, stacktrace {ex.StackTrace}");
				imageFile.Flags.Set(EntryFlags.ThumbnailError);
				return false;
			}
		}

		internal void HighlightBestMatches() {
			// One pass per group: find the best value per metric and mark every item
			// that ties it. Equivalent to the previous sort-and-walk-ties logic, but
			// without re-filtering the whole duplicate set per item and re-sorting per
			// metric, which was quadratic in the number of results.
			foreach (var group in Duplicates.GroupBy(d => d.GroupId)) {
				List<DuplicateItem> items = group.ToList();
				// Groups are homogeneous: images are only ever compared with images.
				bool isImage = items[0].IsImage;

				if (!isImage) {
					TimeSpan bestDuration = items.Max(d => d.Duration);
					foreach (DuplicateItem d in items)
						if (d.Duration == bestDuration) d.IsBestDuration = true;
				}

				long bestSize = items.Min(d => d.SizeLong);
				foreach (DuplicateItem d in items)
					if (d.SizeLong == bestSize) d.IsBestSize = true;

				if (!isImage) {
					float bestFps = items.Max(d => d.Fps);
					foreach (DuplicateItem d in items)
						if (d.Fps == bestFps) d.IsBestFps = true;

					decimal bestBitRate = items.Max(d => d.BitRateKbs);
					foreach (DuplicateItem d in items)
						if (d.BitRateKbs == bestBitRate) d.IsBestBitRateKbs = true;

					int bestAudioSampleRate = items.Max(d => d.AudioSampleRate);
					foreach (DuplicateItem d in items)
						if (d.AudioSampleRate == bestAudioSampleRate) d.IsBestAudioSampleRate = true;

					decimal bestAudioBitRate = items.Max(d => d.AudioBitRateKbs);
					foreach (DuplicateItem d in items)
						if (d.AudioBitRateKbs == bestAudioBitRate) d.IsBestAudioBitRateKbs = true;

					int bestHdrRank = items.Max(d => d.HdrFormatRank);
					foreach (DuplicateItem d in items)
						if (d.HdrFormatRank == bestHdrRank) d.IsBestHdrFormat = true;
				}

				int bestFrameSize = items.Max(d => d.FrameSizeInt);
				foreach (DuplicateItem d in items)
					if (d.FrameSizeInt == bestFrameSize) d.IsBestFrameSize = true;
			}
		}

		public void Pause() {
			if (!isScanning || pauseTokenSource.IsPaused) return;
			Logger.Instance.Info("Scan paused by user");
			ElapsedTimer.Stop();
			SearchTimer.Stop();
			pauseTokenSource.IsPaused = true;
			// Safe suspend point: flush completed work off the UI thread so closing/redeploying now loses nothing
			// (a later rescan resumes from the cache). In-flight files that finish during the pause land in the next save.
			System.Threading.Tasks.Task.Run(FlushDatabase);

		}

		public void Resume() {
			if (!isScanning || pauseTokenSource.IsPaused != true) return;
			Logger.Instance.Info("Scan resumed by user");
			ElapsedTimer.Start();
			SearchTimer.Start();
			pauseTokenSource.IsPaused = false;
		}

		/// <summary>
		/// Stops the scan. During the file-reading phase the FIRST call is a SAFE stop — nothing new
		/// starts, in-flight files finish to 100% (so their fingerprints are cached, not re-paid next
		/// scan), then completed work is saved and the scan ends. A SECOND call, or a call outside that
		/// phase (file list / compare, which have no per-file rework to protect), hard-cancels.
		/// </summary>
		/// <returns>true = safe stop initiated (drain in progress); false = hard cancellation.</returns>
		public bool Stop(bool force = false) {
			if (pauseTokenSource.IsPaused)
				Resume();
			if (!isScanning)
				return false;
			if (!force && driveCounters != null && !stopRequested) {
				stopRequested = true;
				Logger.Instance.Info("Stop requested: letting in-flight files finish safely (press Stop again to force-abort)");
				return true;
			}
			Logger.Instance.Info("Scan stopped by user");
			cancelationTokenSource.Cancel();
			return false;
		}
	}
}
