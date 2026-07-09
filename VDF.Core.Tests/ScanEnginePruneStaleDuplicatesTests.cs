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
using VDF.Core.ViewModels;

namespace VDF.Core.Tests;

// A duplicate deleted through VDF must never come back in a later compare pass (TOMBSTONE-DESIGN.md).
// It used to: the DB entry was purged but the row stayed in ScanEngine.Duplicates, which PartialCompare
// reuses (clearDuplicates:false) and ScanDone re-projects wholesale.
[Collection("Database")]
public class ScanEnginePruneStaleDuplicatesTests {
	static DuplicateItem Row(string path, Guid group) => new() { Path = path, GroupId = group };

	static void SeedDatabase(params string[] paths) {
		DatabaseUtils.Database.Clear();
		foreach (string p in paths)
			DatabaseUtils.Database.Add(new FileEntry { Path = p });
	}

	[Fact]
	public void RemoveFromDatabase_AlsoRetiresTheRowFromTheLiveResultSet() {
		var engine = new ScanEngine();
		var group = Guid.NewGuid();
		SeedDatabase(@"C:\keep.mp4", @"C:\gone.mp4");
		engine.Duplicates.Add(Row(@"C:\keep.mp4", group));
		engine.Duplicates.Add(Row(@"C:\gone.mp4", group));

		Assert.True(engine.RemoveFromDatabase(new FileEntry { Path = @"C:\gone.mp4" }));

		Assert.DoesNotContain(engine.Duplicates, d => d.Path == @"C:\gone.mp4");
		Assert.Contains(engine.Duplicates, d => d.Path == @"C:\keep.mp4");

		DatabaseUtils.Database.Clear();
	}

	[Fact]
	public void RemoveFromDatabase_MatchesPathCaseInsensitivelyOnWindows() {
		if (!CoreUtils.IsWindows)
			return;
		var engine = new ScanEngine();
		SeedDatabase(@"C:\Movie.mp4");
		engine.Duplicates.Add(Row(@"C:\Movie.mp4", Guid.NewGuid()));

		engine.RemoveFromDatabase(new FileEntry { Path = @"c:\movie.MP4" });

		Assert.Empty(engine.Duplicates);
		DatabaseUtils.Database.Clear();
	}

	[Fact]
	public void PruneStaleDuplicates_DropsPurgedRows_CollapsesSingletons_KeepsTombstones() {
		var engine = new ScanEngine();
		var halved = Guid.NewGuid();     // one member purged -> the survivor is no longer a duplicate
		var intact = Guid.NewGuid();     // both members still in the DB -> untouched
		var buried = Guid.NewGuid();     // both members are tombstones (in DB, file gone) -> untouched

		// The tombstone paths are deliberately absent from disk: presence in the DB is what decides.
		SeedDatabase(@"C:\survivor.mp4", @"C:\a.mp4", @"C:\b.mp4", @"C:\ghost1.mp4", @"C:\ghost2.mp4");
		engine.Duplicates.Add(Row(@"C:\survivor.mp4", halved));
		engine.Duplicates.Add(Row(@"C:\purged.mp4", halved));      // deleted through VDF: DB entry gone
		engine.Duplicates.Add(Row(@"C:\a.mp4", intact));
		engine.Duplicates.Add(Row(@"C:\b.mp4", intact));
		engine.Duplicates.Add(Row(@"C:\ghost1.mp4", buried));
		engine.Duplicates.Add(Row(@"C:\ghost2.mp4", buried));

		engine.PruneStaleDuplicates();

		Assert.DoesNotContain(engine.Duplicates, d => d.GroupId == halved);   // ghost row AND its lone survivor
		Assert.Equal(2, engine.Duplicates.Count(d => d.GroupId == intact));
		Assert.Equal(2, engine.Duplicates.Count(d => d.GroupId == buried));   // tombstone group must survive

		// Idempotent: a second pass finds nothing more to do.
		int before = engine.Duplicates.Count;
		engine.PruneStaleDuplicates();
		Assert.Equal(before, engine.Duplicates.Count);

		DatabaseUtils.Database.Clear();
	}
}
