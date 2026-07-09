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

using VDF.Core.Utils;

namespace VDF.Core.Tests.Utils;

// SaveDatabase must leave a FRESH one-generation rollback point: File.Replace atomically swaps in the
// new DB and demotes the previous one to <db>.bak (a rename, not a copy). Guards against regressing to
// a plain File.Move (which deletes the old DB and leaves .bak stale/orphaned) and against the .bak
// silently going missing.
[Collection("Database")]   // DatabaseUtils.Database is global state; don't race the other DB tests
public class DatabaseBackupTests {
	static string Asset(string name) =>
		Path.Combine(AppContext.BaseDirectory, "TestAssets", name);

	[Fact]
	public void SaveDatabase_DemotesPreviousDbToBak_AsAFreshRollbackPoint() {
		string dir = Path.Combine(Path.GetTempPath(), $"vdf-dbbak-{Guid.NewGuid():N}");
		Directory.CreateDirectory(dir);
		try {
			string dbPath = Path.Combine(dir, "ScannedFiles.db");
			string bakPath = dbPath + ".bak";
			File.Copy(Asset("legacy-wrapper.db"), dbPath);
			byte[] preSaveBytes = File.ReadAllBytes(dbPath);   // the DB about to be replaced

			DatabaseUtils.CustomDatabaseFolder = dir;
			DatabaseUtils.InvalidateDatabaseFolder();
			Assert.True(DatabaseUtils.LoadDatabase());
			DatabaseUtils.SaveDatabase();   // File.Replace: new format in, previous rolls to .bak

			// The previous DB is preserved verbatim as the rollback point (not stale, not lost).
			Assert.True(File.Exists(bakPath));
			Assert.Equal(preSaveBytes, File.ReadAllBytes(bakPath));
			// The temp is consumed by the atomic replace, and the live DB reloads fine.
			Assert.False(File.Exists(Path.Combine(dir, "ScannedFiles_new.db")));
			DatabaseUtils.Database.Clear();
			Assert.True(DatabaseUtils.LoadDatabase());
			Assert.Equal(3, DatabaseUtils.Database.Count);

			// A second save rotates again: .bak now holds the first (new-format) save.
			byte[] firstSave = File.ReadAllBytes(dbPath);
			DatabaseUtils.SaveDatabase();
			Assert.Equal(firstSave, File.ReadAllBytes(bakPath));
		}
		finally {
			DatabaseUtils.CustomDatabaseFolder = null;
			DatabaseUtils.InvalidateDatabaseFolder();
			DatabaseUtils.Database.Clear();
			try { Directory.Delete(dir, recursive: true); } catch { }
		}
	}
}
