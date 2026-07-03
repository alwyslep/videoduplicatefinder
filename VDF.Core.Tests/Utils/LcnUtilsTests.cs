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

public class LcnUtilsTests {
	[Fact]
	public void MissingFile_ReturnsMaxValue_SortsToEnd() {
		Assert.Equal(long.MaxValue, LcnUtils.GetFirstLcn(Path.Combine(Path.GetTempPath(), "vdf-lcn-does-not-exist.bin")));
	}

	[Fact]
	public void RealFile_OnWindows_ReturnsAPosition() {
		if (!OperatingSystem.IsWindows()) return;
		string path = Path.Combine(Path.GetTempPath(), $"vdf-lcn-{Guid.NewGuid():N}.bin");
		try {
			// 300KB: large enough to be non-resident on NTFS, so a real extent exists.
			File.WriteAllBytes(path, new byte[300 * 1024]);
			long lcn = LcnUtils.GetFirstLcn(path);
			Assert.True(lcn >= 0);
			Assert.NotEqual(long.MaxValue, lcn);
		}
		finally { File.Delete(path); }
	}
}
