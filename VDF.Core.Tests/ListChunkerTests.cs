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

namespace VDF.Core.Tests;

// Balanced chunking of a compare group so GridPlayer never loads more than maxSize videos at once.
// The invariants that matter: cap respected, nothing lost/reordered, and NO lone-item chunk (which
// would be a useless 1-video compare window) whenever the group itself has ≥2 items.
public class ListChunkerTests {
	static List<List<int>> Chunk(int n, int maxSize) =>
		ListChunker.ChunkEvenly(Enumerable.Range(0, n).ToList(), maxSize).ToList();

	[Fact]
	public void Multiple_Of_Cap_Splits_Evenly() {
		var chunks = Chunk(20, 4);
		Assert.Equal(5, chunks.Count);
		Assert.All(chunks, c => Assert.Equal(4, c.Count));
	}

	[Fact]
	public void Remainder_Is_Balanced_Not_Trailing_Singleton() {
		// 5 must be [3,2], never [4,1] — a lone-item window can't be compared.
		Assert.Equal(new[] { 3, 2 }, Chunk(5, 4).Select(c => c.Count));
		Assert.Equal(new[] { 4, 3 }, Chunk(7, 4).Select(c => c.Count));
		Assert.Equal(new[] { 3, 3, 3 }, Chunk(9, 4).Select(c => c.Count));
	}

	[Theory]
	[InlineData(2)]
	[InlineData(3)]
	[InlineData(5)]
	[InlineData(6)]
	[InlineData(7)]
	[InlineData(13)]
	[InlineData(20)]
	[InlineData(37)]
	public void No_Chunk_Below_Two_And_None_Over_Cap(int n) {
		foreach (var c in Chunk(n, 4)) {
			Assert.True(c.Count >= 2, $"n={n}: lone-item chunk");
			Assert.True(c.Count <= 4, $"n={n}: chunk over cap");
		}
	}

	[Fact]
	public void Preserves_Order_And_Loses_Nothing() {
		var flat = Chunk(37, 4).SelectMany(c => c).ToList();
		Assert.Equal(Enumerable.Range(0, 37).ToList(), flat);
	}

	[Fact]
	public void Small_Groups_Are_Untouched() {
		Assert.Equal(new[] { 2 }, Chunk(2, 4).Select(c => c.Count));
		Assert.Equal(new[] { 4 }, Chunk(4, 4).Select(c => c.Count));
	}
}
