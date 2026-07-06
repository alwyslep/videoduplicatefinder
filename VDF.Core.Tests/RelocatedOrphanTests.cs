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

namespace VDF.Core.Tests;

// Verifies the pure selection at the heart of PruneRelocatedOrphans: a gone entry is an orphan
// only when its exact content (OsHash) still exists among the live files.
public class RelocatedOrphanTests {
	static FileEntry Entry(string path, string? osHash) => new() { Path = path, OsHash = osHash };

	[Fact]
	public void GoneEntry_WithLiveTwin_IsSelected() {
		var live = new HashSet<string>(StringComparer.Ordinal) { "hashA" };
		var gone = new[] { Entry(@"D:\old\a.mp4", "hashA") };
		var orphans = ScanEngine.SelectRelocatedOrphans(live, gone);
		Assert.Single(orphans);
		Assert.Equal(@"D:\old\a.mp4", orphans[0].Path);
	}

	[Fact]
	public void GoneEntry_NoLiveTwin_IsKept() {
		// Genuinely deleted content (no live copy) -> a real tombstone, must survive.
		var live = new HashSet<string>(StringComparer.Ordinal) { "hashLive" };
		var gone = new[] { Entry(@"D:\old\a.mp4", "hashGone") };
		Assert.Empty(ScanEngine.SelectRelocatedOrphans(live, gone));
	}

	[Fact]
	public void GoneEntry_NullOsHash_IsKept() {
		// Pre-oshash entry: unmatchable, never removed (can't prove the content lives on).
		var live = new HashSet<string>(StringComparer.Ordinal) { "hashA" };
		var gone = new[] { Entry(@"D:\old\a.mp4", null) };
		Assert.Empty(ScanEngine.SelectRelocatedOrphans(live, gone));
	}

	[Fact]
	public void MixedSet_SelectsOnlyLiveTwins() {
		var live = new HashSet<string>(StringComparer.Ordinal) { "h1", "h3" };
		var gone = new[] {
			Entry(@"D:\old\1.mp4", "h1"),   // twin live -> orphan
			Entry(@"D:\old\2.mp4", "h2"),   // no twin  -> keep
			Entry(@"D:\old\3.mp4", "h3"),   // twin live -> orphan
			Entry(@"D:\old\4.mp4", null),   // null      -> keep
		};
		var orphans = ScanEngine.SelectRelocatedOrphans(live, gone);
		Assert.Equal(2, orphans.Count);
		Assert.Contains(orphans, o => o.Path == @"D:\old\1.mp4");
		Assert.Contains(orphans, o => o.Path == @"D:\old\3.mp4");
	}
}
