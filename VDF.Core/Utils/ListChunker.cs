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

namespace VDF.Core.Utils {
	public static class ListChunker {
		/// <summary>
		/// Split a list into ceil(n/maxSize) chunks whose sizes differ by at most 1 (balanced),
		/// preserving order. Unlike Enumerable.Chunk (which trails a small remainder, e.g. 5→[4,1]),
		/// this never yields a lone-item chunk when the whole is ≥2 (5→[3,2]) — so every chunk stays
		/// a valid ≥2 comparison set for the GridPlayer compare view. maxSize must be ≥1.
		/// </summary>
		public static IEnumerable<List<T>> ChunkEvenly<T>(IReadOnlyList<T> items, int maxSize) {
			if (maxSize < 1) throw new ArgumentOutOfRangeException(nameof(maxSize));
			int n = items.Count;
			if (n == 0) yield break;
			int k = (n + maxSize - 1) / maxSize;          // number of chunks
			int baseSize = n / k, extra = n % k, idx = 0; // first `extra` chunks get one more
			for (int c = 0; c < k; c++) {
				int size = baseSize + (c < extra ? 1 : 0);
				var chunk = new List<T>(size);
				for (int j = 0; j < size; j++) chunk.Add(items[idx++]);
				yield return chunk;
			}
		}
	}
}
