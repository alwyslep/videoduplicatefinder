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

public class NormalizePathEntryTests {
	[Theory]
	[InlineData("*temp*")]            // bare segment pattern (#582) — GetFullPath would prefix CWD
	[InlineData("cache?")]
	[InlineData(@"D:\stuff\*cache*")] // rooted pattern
	public void WildcardEntries_PassThroughVerbatim(string entry) {
		Assert.Equal(entry, ScanEngine.NormalizePathEntry(entry));
	}

	[Fact]
	public void TrailingSeparator_IsTrimmed() {
		string path = Path.Combine(Path.GetTempPath(), "VdfNormTest");
		Assert.Equal(path, ScanEngine.NormalizePathEntry(path + Path.DirectorySeparatorChar));
	}

	[Fact]
	public void DriveRoot_KeepsItsSeparator() {
		if (!OperatingSystem.IsWindows()) return;
		Assert.Equal(@"C:\", ScanEngine.NormalizePathEntry(@"C:\"));
	}

	[Fact]
	public void RelativePath_BecomesAbsolute() {
		Assert.True(Path.IsPathRooted(ScanEngine.NormalizePathEntry("somefolder")));
	}
}
