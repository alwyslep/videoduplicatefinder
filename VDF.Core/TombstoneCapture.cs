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

using MemoryPack;
using VDF.Core.FFTools;
using VDF.Core.Utils;

namespace VDF.Core {
	/// <summary>
	/// Delete-time fingerprint capture for files removed OUTSIDE VDF (e.g. the mpv player).
	/// A file deleted before it was ever scanned leaves no tombstone — capturing a
	/// scan-equivalent FileEntry at delete time closes that gap: the external tool (DbProbe
	/// "capture") parks the snapshot in DeletedThumbs.sqlite and the GUI ingests it into the
	/// database at the next scan, where the normal tombstone machinery (pHash re-download
	/// match, "already deleted" badge) picks it up. TOMBSTONE-DESIGN.md.
	/// </summary>
	public static class TombstoneCapture {
		public enum IngestResult { Added, SkippedPathKnown, SkippedContentKnown, Invalid }

		/// <summary>
		/// The visible frames captured at delete time, keyed (path, oshash) → JPEGs. Set by the
		/// GUI (DeletedThumbsStore.LoadThumbs); the thumbnail loaders use it so a tombstone row
		/// still shows what was thrown away instead of a blank.
		/// </summary>
		public static Func<string, string?, List<byte[]>?>? MissingFileThumbnailProvider;

		/// <summary>
		/// Builds a scan-equivalent snapshot (mediaInfo + grayBytes/pHashes at the standard
		/// (i+1)/(N+1) positions + OsHash) of a still-existing file. Same sampling code path as
		/// GatherInfos, so the grayBytes KEYS match what a later scan with the same settings
		/// expects. Returns null with a reason when the file can't be fingerprinted.
		/// </summary>
		public static FileEntry? CaptureEntry(string path, int thumbnailCount, double maxSamplingDurationSeconds, out string? error) {
			error = null;
			FileInfo fileInfo = new(path);
			if (!fileInfo.Exists) { error = "file not found"; return null; }
			var entry = new FileEntry(fileInfo);
			if (entry.IsImage) { error = "image files are not captured"; return null; }

			var info = FFProbeEngine.GetMediaInfo(path, extendedLogging: false);
			if (info == null) { error = "ffprobe could not read media information"; return null; }
			entry.mediaInfo = info;

			var positions = SamplePositions(thumbnailCount);
			bool sampled = FfmpegEngine.GetGrayBytesFromVideo(entry, positions, maxSamplingDurationSeconds, extendedLogging: false);
			// Same rescue as GatherInfos: a container inflated by a long audio track / cover art
			// seeks past the video's end; a fresh probe carries VideoDurationSeconds for mapped seeks.
			if (!sampled && entry.mediaInfo.VideoDurationSeconds == 0) {
				var fresh = FFProbeEngine.GetMediaInfo(path, extendedLogging: false);
				if (fresh != null && fresh.VideoDurationSeconds > 0 &&
					fresh.VideoDurationSeconds < fresh.Duration.TotalSeconds) {
					entry.mediaInfo = fresh;
					entry.Flags.Set(EntryFlags.ThumbnailError, false);
					sampled = FfmpegEngine.GetGrayBytesFromVideo(entry, positions, maxSamplingDurationSeconds, extendedLogging: false);
				}
			}
			if (!sampled) {
				error = entry.Flags.Has(EntryFlags.TooDark) ? "all sampled frames too dark" : "frame sampling failed";
				return null;
			}
			entry.OsHash = OsHashUtils.TryCompute(path);
			return entry;
		}

		/// <summary>The scan's frame sample positions: (i+1)/(N+1) — keep in sync with BuildFileList.</summary>
		public static List<float> SamplePositions(int thumbnailCount) {
			var positions = new List<float>(thumbnailCount);
			float positionCounter = 0f;
			for (int i = 0; i < thumbnailCount; i++) {
				positionCounter += 1.0F / (thumbnailCount + 1);
				positions.Add(positionCounter);
			}
			return positions;
		}

		public static byte[] Serialize(FileEntry entry) => MemoryPackSerializer.Serialize(entry);

		/// <summary>Loads the database unless this process already has it in memory.</summary>
		public static void EnsureDatabaseLoaded() {
			if (DatabaseUtils.Database.Count == 0)
				DatabaseUtils.LoadDatabase();
		}

		/// <summary>OsHashes of every current DB entry — build once per ingest batch.</summary>
		public static HashSet<string> CollectDatabaseOsHashes() {
			var set = new HashSet<string>(StringComparer.Ordinal);
			foreach (var e in DatabaseUtils.Database)
				if (e.OsHash != null)
					set.Add(e.OsHash);
			return set;
		}

		/// <summary>
		/// Adds one captured snapshot to the in-memory database as a tombstone-to-be.
		/// Content already known (same path OR same OsHash) is skipped — the existing entry
		/// already carries the fingerprint. Caller persists via ScanEngine.SaveDatabase().
		/// </summary>
		public static IngestResult TryIngestCapturedEntry(byte[] blob, HashSet<string> knownOsHashes) {
			FileEntry? entry;
			try { entry = MemoryPackSerializer.Deserialize<FileEntry>(blob); }
			catch { return IngestResult.Invalid; }
			if (entry == null || string.IsNullOrEmpty(entry.Path) || entry.mediaInfo == null ||
				entry.grayBytes == null || entry.grayBytes.Count == 0)
				return IngestResult.Invalid;
			if (DatabaseUtils.Database.Contains(entry))
				return IngestResult.SkippedPathKnown;
			if (entry.OsHash != null && knownOsHashes.Contains(entry.OsHash))
				return IngestResult.SkippedContentKnown;
			DatabaseUtils.Database.Add(entry);
			if (entry.OsHash != null)
				knownOsHashes.Add(entry.OsHash);
			return IngestResult.Added;
		}
	}
}
