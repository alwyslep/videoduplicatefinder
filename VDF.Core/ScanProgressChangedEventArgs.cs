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

using System;

namespace VDF.Core {
	public struct ScanProgressChangedEventArgs {

		public string CurrentFile;
		public int CurrentPosition;
		public int MaxPosition;
		public TimeSpan Elapsed;
		public TimeSpan Remaining;
		/// <summary>Short label describing what's happening to CurrentFile right now (e.g. "probing", "sampling frames", "audio fingerprint"). Empty between files.</summary>
		public string CurrentStage;
		/// <summary>Progress within the current stage (e.g. sample 2 of 5). Both zero when stage progress isn't tracked.</summary>
		public int StageCurrent;
		public int StageMax;
		/// <summary>Per-physical-drive scan progress (bytes/files) for the segmented status bar. Null outside the file-reading (GatherInfos) phase.</summary>
		public DriveProgress[]? Drives;
	}

	/// <summary>One drive's scan progress. Root is Path.GetPathRoot (e.g. "I:\\"); the segment's width is
	/// proportional to TotalBytes and its fill to DoneBytes/TotalBytes.</summary>
	public struct DriveProgress {
		public string Root;
		public long TotalBytes;
		public long DoneBytes;
		public int TotalFiles;
		public int DoneFiles;
		/// <summary>Adaptive controller's last measured files/sec for this drive (0 in static mode).</summary>
		public double FilesPerSec;
		/// <summary>Adaptive controller's current worker concurrency for this drive (0 in static mode).</summary>
		public int Concurrency;
		/// <summary>One row per worker currently mid-file on this drive — length tracks live concurrency,
		/// so a drive running N workers shows N rows. Empty when the drive is idle or finished.</summary>
		public DriveActiveFile[]? ActiveFiles;
		/// <summary>Files that actually ran a tool this scan (ffprobe/frame sampling/audio fingerprint)
		/// — everything else completed instantly from cache or flags. Explains a fast-filling bar.</summary>
		public int Analyzed;
		/// <summary>Entries whose file no longer exists at its recorded path (skipped in milliseconds).</summary>
		public int Missing;
	}

	/// <summary>One worker's in-progress file on a drive, for a per-drive "now processing" row.</summary>
	public struct DriveActiveFile {
		public string File;
		/// <summary>Sub-stage of File (e.g. "audio fingerprint"); null/empty when untracked.</summary>
		public string? Stage;
		public int StageCurrent;
		public int StageMax;
	}
}
