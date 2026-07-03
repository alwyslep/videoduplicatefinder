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
//     along with VideoDuplicateFinder.  If not, see <https://www.gnu.org/licenses/>.
// */
//

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using VDF.Core;
using VDF.Core.Utils;
using VDF.GUI.Data;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Utils {
	/// <summary>
	/// Append-only visual registry of the unique files that SURVIVE a duplicate deletion — the
	/// mirror image of tombstones (which remember what was rejected). One mid-video frame per
	/// surviving file, keyed by content (OsHash) so the record outlives later moves/renames.
	/// Lives in its own SQLite file next to ScannedFiles.db; never read by the scanner itself.
	/// </summary>
	internal static class SurvivorThumbArchive {
		const int ThumbMaxWidth = 320;
		// Callers fire-and-forget from the delete flow; two rapid deletes must not race one DB file.
		static readonly object writeLock = new();

		static string DbPath => Path.Combine(
			CoreUtils.ResolveDatabaseFolder(SettingsFile.Instance.CustomDatabaseFolder),
			"SurvivorThumbs.sqlite");

		/// <summary>Archives one frame per survivor; content already archived is skipped. Returns rows added.</summary>
		public static int Archive(IReadOnlyList<DuplicateItemVM> survivors) {
			lock (writeLock)
				return ArchiveLocked(survivors);
		}

		static int ArchiveLocked(IReadOnlyList<DuplicateItemVM> survivors) {
			using var con = new SqliteConnection($"Data Source={DbPath}");
			con.Open();
			using (var create = con.CreateCommand()) {
				create.CommandText = @"CREATE TABLE IF NOT EXISTS survivors(
					oshash       TEXT PRIMARY KEY,
					path         TEXT NOT NULL,
					size         INTEGER NOT NULL,
					duration_sec REAL NOT NULL,
					thumb        BLOB,
					added_utc    TEXT NOT NULL)";
				create.ExecuteNonQuery();
			}
			int added = 0;
			foreach (var s in survivors) {
				string path = s.ItemInfo.Path;
				if (!File.Exists(path)) continue;
				string? oshash = OsHashUtils.TryCompute(path);
				if (oshash == null) continue;   // tiny/locked file — no content key, no record
				// Check before paying for the ffmpeg frame grab.
				using (var check = con.CreateCommand()) {
					check.CommandText = "SELECT 1 FROM survivors WHERE oshash = $h";
					check.Parameters.AddWithValue("$h", oshash);
					if (check.ExecuteScalar() != null) continue;
				}
				byte[]? thumb = ScanEngine.ExtractThumbnailJpeg(path,
					TimeSpan.FromTicks(s.ItemInfo.Duration.Ticks / 2), ThumbMaxWidth);
				using var insert = con.CreateCommand();
				insert.CommandText = @"INSERT OR IGNORE INTO survivors(oshash, path, size, duration_sec, thumb, added_utc)
					VALUES($h, $p, $s, $d, $t, $u)";
				insert.Parameters.AddWithValue("$h", oshash);
				insert.Parameters.AddWithValue("$p", path);
				insert.Parameters.AddWithValue("$s", s.ItemInfo.SizeLong);
				insert.Parameters.AddWithValue("$d", s.ItemInfo.Duration.TotalSeconds);
				insert.Parameters.AddWithValue("$t", (object?)thumb ?? DBNull.Value);
				insert.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
				added += insert.ExecuteNonQuery();
			}
			return added;
		}
	}
}
