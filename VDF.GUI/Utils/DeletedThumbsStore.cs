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

namespace VDF.GUI.Utils {
	/// <summary>
	/// Reader side of the delete-time capture store (DeletedThumbs.sqlite, written by the
	/// external DbProbe "capture" command just before a file is deleted in mpv). Two duties:
	///  - IngestPendingCaptures: at scan start, park captured fingerprints into the scan DB so
	///    a never-scanned file deleted outside VDF still gets a re-download-catching tombstone.
	///  - LoadThumbs: the visible 4-frame record for tombstone rows (file gone, frames kept).
	/// Mirror image of SurvivorThumbArchive. TOMBSTONE-DESIGN.md.
	/// </summary>
	internal static class DeletedThumbsStore {
		static readonly object gate = new();

		static string DbPath => Path.Combine(
			CoreUtils.ResolveDatabaseFolder(SettingsFile.Instance.CustomDatabaseFolder),
			"DeletedThumbs.sqlite");

		/// <summary>Ingests pending captured fingerprints into the scan database. Returns entries added.</summary>
		public static int IngestPendingCaptures() {
			lock (gate) {
				try {
					if (!File.Exists(DbPath)) return 0;
					return IngestLocked();
				}
				catch (Exception ex) {
					Logger.Instance.Info($"Deleted-capture ingest failed: {ex.Message}");
					return 0;
				}
			}
		}

		static int IngestLocked() {
			using var con = new SqliteConnection($"Data Source={DbPath}");
			con.Open();
			// ingested: 0=pending, 1=done or already known, 2=invalid blob (never retried)
			var pending = new List<(long rowid, byte[] blob)>();
			using (var select = con.CreateCommand()) {
				select.CommandText = "SELECT rowid, entry FROM deleted WHERE ingested = 0 AND entry IS NOT NULL";
				using var r = select.ExecuteReader();
				while (r.Read())
					pending.Add((r.GetInt64(0), (byte[])r[1]));
			}
			if (pending.Count == 0) return 0;

			TombstoneCapture.EnsureDatabaseLoaded();
			var known = TombstoneCapture.CollectDatabaseOsHashes();
			int added = 0;
			var done = new List<(long rowid, int state)>(pending.Count);
			foreach (var (rowid, blob) in pending) {
				var result = TombstoneCapture.TryIngestCapturedEntry(blob, known);
				if (result == TombstoneCapture.IngestResult.Added) added++;
				done.Add((rowid, result == TombstoneCapture.IngestResult.Invalid ? 2 : 1));
			}
			// Persist BEFORE marking rows, so a crash between the two just retries the ingest
			// (re-adding is idempotent: the entry is then path-known and skipped).
			if (added > 0)
				ScanEngine.SaveDatabase();
			using (var tx = con.BeginTransaction())
			using (var update = con.CreateCommand()) {
				update.Transaction = tx;
				update.CommandText = "UPDATE deleted SET ingested = $s WHERE rowid = $r";
				var pS = update.CreateParameter(); pS.ParameterName = "$s"; update.Parameters.Add(pS);
				var pR = update.CreateParameter(); pR.ParameterName = "$r"; update.Parameters.Add(pR);
				foreach (var (rowid, state) in done) {
					pS.Value = state;
					pR.Value = rowid;
					update.ExecuteNonQuery();
				}
				tx.Commit();
			}
			Logger.Instance.Info($"Deleted-capture ingest: {added} tombstone(s) added, {pending.Count - added} already known/invalid.");
			return added;
		}

		/// <summary>Captured frames for a tombstone row — content key first, path fallback.</summary>
		public static List<byte[]>? LoadThumbs(string path, string? oshash) {
			lock (gate) {
				try {
					if (!File.Exists(DbPath)) return null;
					using var con = new SqliteConnection($"Data Source={DbPath}");
					con.Open();
					using var select = con.CreateCommand();
					select.CommandText = oshash != null
						? "SELECT thumb0, thumb1, thumb2, thumb3 FROM deleted WHERE oshash = $h OR path = $p LIMIT 1"
						: "SELECT thumb0, thumb1, thumb2, thumb3 FROM deleted WHERE path = $p LIMIT 1";
					if (oshash != null)
						select.Parameters.AddWithValue("$h", oshash);
					select.Parameters.AddWithValue("$p", path);
					using var r = select.ExecuteReader();
					if (!r.Read()) return null;
					var list = new List<byte[]>(4);
					for (int i = 0; i < 4; i++)
						if (!r.IsDBNull(i) && r[i] is byte[] { Length: > 0 } b)
							list.Add(b);
					return list.Count > 0 ? list : null;
				}
				catch { return null; }   // thumbnail fallback is best-effort — never break the loader
			}
		}
	}
}
