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


namespace VDF.Core {
	public enum FolderMatchMode { None, SameFolderOnly, DifferentFolderOnly }

	public sealed class Settings {
		// Settable so System.Text.Json can populate these from --settings JSON; without
		// a setter STJ silently leaves them empty even with IncludeFields=true (read-only
		// collection properties aren't repopulated by the default object converter).
		public HashSet<string> IncludeList { get; set; } = new HashSet<string>();
		public HashSet<string> BlackList { get; set; } = new HashSet<string>();

		public bool IgnoreReadOnlyFolders;
		public bool IgnoreReparsePoints;
		public bool ExcludeHardLinks;
		public bool GeneratePreviewThumbnails;
		public bool UseNativeFfmpegBinding;
		public bool IncludeSubDirectories = true;
		public bool IncludeImages = true;
		public bool ExtendedFFToolsLogging;
		public bool LogExcludedFiles;
		public bool AlwaysRetryFailedSampling;
		// With AlwaysRetryFailedSampling on, a file whose frame sampling keeps failing (corrupt/
		// truncated — it will never decode) would otherwise be re-decoded every scan: pure waste.
		// After this many consecutive failed attempts it is treated as permanently failed and skipped
		// even with retry on; transient failures (locked/offline) still get this many chances first.
		// The count rides on the entry, so a moved file keeps it. 0 = no cap (retry forever, old behaviour).
		public int MaxSamplingRetryAttempts = 2;
		// Opt-in (default OFF, destructive): at scan end, move videos whose frame sampling stayed
		// permanently failed (ThumbnailError + SamplingFailCount >= MaxSamplingRetryAttempts, i.e. a
		// genuinely undecodable/corrupt video that survived the retry budget AND the re-probe rescue)
		// to the RECYCLE BIN and drop their DB entry. Recoverable by design — a rare "VDF can't decode
		// this codec but the file is fine" case is undeletably lost only if permanently deleted, which
		// this never does. Audio state is irrelevant (a corrupt video is garbage even with good audio).
		public bool AutoDeleteUnrecoverableFiles;
		public bool IgnoreBlackPixels;
		public bool IgnoreWhitePixels;
		public bool CompareHorizontallyFlipped;
		public bool IncludeNonExistingFiles;
		public bool ScanAgainstEntireDatabase;
		public FolderMatchMode FolderMatchMode;
		public int SameFolderDepth = 1;
		public bool UsePHashing;
		public bool UseExifCreationDate;
		public string LanguageCode = "en";

		public FFTools.FFHardwareAccelerationMode HardwareAccelerationMode;

		public byte Threshhold = 5;
		public float Percent = 96f;
		public double PercentDurationDifference = 20d;
		public double DurationDifferenceMinSeconds;
		public double DurationDifferenceMaxSeconds;
		public double MaxSamplingDurationSeconds;

		public int ThumbnailCount = 1;
		/// <summary>Maximum width in pixels for display thumbnails (0 = original resolution).</summary>
		public int ThumbnailMaxWidth = 100;
		public int MaxDegreeOfParallelism = 1;
		// Concurrency for spindle HDDs (per-drive); fast SSD/NVMe drives use MaxDegreeOfParallelism instead.
		public int HddMaxDegreeOfParallelism = 2;
		// Adaptive per-drive concurrency: each drive AIMD-tunes its worker count in [1, fair-share ceiling]
		// from its measured files/sec; total decodes are capped at Environment.ProcessorCount. Falls back to the
		// static per-device split when off. Per-drive hard caps come live from the status-bar dropdowns (SetDriveCap).
		public bool AdaptiveConcurrency = true;
		public int AdaptiveWindowSeconds = 120;
		// User-chosen per-drive worker caps (root -> cap), seeded into the drive counters at scan START so a
		// capped drive launches at its cap instead of at auto — the GUI's live SetDriveCap push only lands
		// after the first progress event, by which time an uncapped launch has already spun up extra workers
		// that then have to drain (visible as "starts at 4, shrinks to 1" after every restart).
		public Dictionary<string, int> DriveWorkerCaps = new(StringComparer.OrdinalIgnoreCase);
		// Drive roots whose status-bar pause checkbox is unchecked, seeded at scan START so a
		// paused drive never probes, never LCN-sorts and never claims a single file — the GUI's
		// live push (progress ticks) only covers mid-scan toggles, which raced the first files.
		public HashSet<string> DriveDisabledDrives { get; set; } = new(StringComparer.OrdinalIgnoreCase);

		// Segment-parallel audio fingerprint decode: 0 or 1 = existing sequential path
		// (default), N>1 = decode each file in parallel segments with a process-wide
		// N-thread cap. Requires the native FFmpeg binding; unsupported files fall
		// back to the sequential path per-file. See PARALLEL-AUDIO-DECODE-DESIGN.md.
		public int ParallelAudioDecodeThreads;

		public string CustomFFArguments = string.Empty;
		public string CustomDatabaseFolder = string.Empty;

		public bool FilterByFilePathContains;
		public List<string> FilePathContainsTexts = new();
		public bool FilterByFilePathNotContains;
		public List<string> FilePathNotContainsTexts = new();
		public bool FilterByFileSize;
		public int MaximumFileSize;
		public int MinimumFileSize;

		// ── Partial clip detection ──────────────────────────────────────────────
		/// <summary>Enable audio-fingerprint-based partial clip detection.</summary>
		public bool EnablePartialClipDetection;
		/// <summary>
		/// Minimum ratio of clip-duration / source-duration for a pair to be a candidate.
		/// Default 0.10 (clip must be at least 10% of the longer video).
		/// </summary>
		public double PartialClipMinRatio = 0.10;
		/// <summary>
		/// Minimum average Hamming similarity (0–1) for a sliding-window match to be
		/// accepted as a partial clip.  Default 0.80.
		/// </summary>
		public double PartialClipSimilarityThreshold = 0.80;
		/// <summary>
		/// When true, partial clip matches must also pass a visual frame check at the
		/// matched offset. Suppresses false positives from videos sharing the same audio
		/// (e.g. TikToks reusing a song) but with different visual content.
		/// </summary>
		public bool PartialClipRequireVisualMatch = true;
		/// <summary>
		/// Minimum visual similarity (0–1) for the on-demand frame check used by
		/// <see cref="PartialClipRequireVisualMatch"/>.  Default 0.85.
		/// Compared via pHash when <see cref="UsePHashing"/> is enabled, otherwise via
		/// 32×32 grayscale percentage difference.
		/// </summary>
		public double PartialClipVisualThreshold = 0.85;

		// ── Database checkpoints ────────────────────────────────────────────
		/// <summary>
		/// Interval in minutes between automatic database saves during scanning.
		/// 0 = disabled (only save at phase boundaries). Default 5.
		/// </summary>
		public int DatabaseCheckpointIntervalMinutes = 5;

		/// <summary>
		/// Returns the allowed duration tolerance in seconds for a video of the given duration,
		/// based on <see cref="PercentDurationDifference"/>, <see cref="DurationDifferenceMinSeconds"/>,
		/// and <see cref="DurationDifferenceMaxSeconds"/>. When the percent rule is disabled (0%),
		/// the seconds bounds act as a flat tolerance so users can run a seconds-only comparison.
		/// </summary>
		internal double GetDurationToleranceSeconds(double durationSeconds) {
			if (PercentDurationDifference > 0) {
				double toleranceSeconds = durationSeconds * PercentDurationDifference / 100d;
				if (DurationDifferenceMinSeconds > 0)
					toleranceSeconds = Math.Max(toleranceSeconds, DurationDifferenceMinSeconds);
				if (DurationDifferenceMaxSeconds > 0)
					toleranceSeconds = Math.Min(toleranceSeconds, DurationDifferenceMaxSeconds);
				return Math.Max(0d, toleranceSeconds);
			}
			// Percent rule disabled: tolerance comes solely from the seconds bounds. Without a
			// percent term, Max would otherwise pin the tolerance to 0; instead take the largest
			// enabled bound so a seconds-only setup behaves like a flat tolerance.
			return Math.Max(0d, Math.Max(DurationDifferenceMinSeconds, DurationDifferenceMaxSeconds));
		}
	}
}
