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

using System.Collections.Concurrent;

namespace VDF.Core.Tests;

/// <summary>
/// The indexed partial-clip matcher (CSR index + sorted vote buffer) must return exactly the pairs the
/// brute sliding-window pass finds. Guards the 2026-07-14 memory rewrite: the vote Dictionary became a
/// sorted long buffer and the postings Lists became flat arrays, so the candidate/verify logic is the
/// thing that could silently drift.
/// </summary>
public class PartialClipIndexedMatcherTests {

	const int SourceBlocks = 600;      // ~10 min sources
	const int ClipBlocks = 200;        // ~3.3 min clips (ratio 0.33: inside the gates)

	static FileEntry Video(string path, uint[] fp) => new() {
		Path = path,
		mediaInfo = new MediaInfo { Duration = TimeSpan.FromSeconds(fp.Length) },
		AudioFingerprint = fp,
	};

	/// <summary>Random non-zero blocks — zero means silence and is skipped by both matchers.</summary>
	static uint[] RandomFingerprint(Random rnd, int blocks) {
		var fp = new uint[blocks];
		for (int i = 0; i < blocks; i++) {
			uint b;
			do { b = (uint)rnd.NextInt64(1, uint.MaxValue); } while (b == 0);
			fp[i] = b;
		}
		return fp;
	}

	/// <summary>A clip cut out of <paramref name="source"/> at <paramref name="offset"/>, with a bit of
	/// re-encode noise (one flipped bit in every 10th block).</summary>
	static uint[] ClipOf(uint[] source, int offset, int blocks) {
		var clip = new uint[blocks];
		Array.Copy(source, offset, clip, 0, blocks);
		for (int i = 0; i < blocks; i += 10)
			clip[i] ^= 1u;
		return clip;
	}

	/// <summary>The brute path from ScanForPartialDuplicates, verbatim gates, as the oracle.</summary>
	static List<(int src, int clip)> BrutePairs(List<FileEntry> videos, Settings settings, float simThreshold) {
		var pairs = new List<(int, int)>();
		for (int i = 0; i < videos.Count - 1; i++) {
			double sourceSec = videos[i].mediaInfo!.Duration.TotalSeconds;
			if (sourceSec < 1.0) continue;
			for (int j = i + 1; j < videos.Count; j++) {
				double clipSec = videos[j].mediaInfo!.Duration.TotalSeconds;
				if (clipSec < 1.0) continue;
				if (clipSec / sourceSec < settings.PartialClipMinRatio) continue;
				if (clipSec / sourceSec >= 0.95) continue;
				uint[] fpSource = videos[i].AudioFingerprint!;
				uint[] fpClip = videos[j].AudioFingerprint!;
				if (fpClip.Length >= fpSource.Length) continue;
				var (sim, _) = ScanEngine.SlidingWindowCompare(fpClip, fpSource, simThreshold);
				if (sim >= simThreshold) pairs.Add((i, j));
			}
		}
		return pairs;
	}

	[Fact]
	public void IndexedMatcher_FindsPlantedClips_AndAgreesWithBrute() {
		var rnd = new Random(20260714);
		const float simThreshold = 0.80f;

		// videos[] is ordered by duration DESC, as ScanForPartialDuplicates builds it:
		// 12 sources first, then 5 clips carved out of sources 0, 2, 4, 7 and 11.
		var sources = new List<FileEntry>();
		for (int i = 0; i < 12; i++)
			sources.Add(Video($@"C:\src{i}.mp4", RandomFingerprint(rnd, SourceBlocks)));

		var planted = new (int srcIdx, int offset)[] { (0, 0), (2, 137), (4, 400), (7, 55), (11, 399) };
		var videos = new List<FileEntry>(sources);
		var expected = new List<(int src, int clip, int offset)>();
		for (int k = 0; k < planted.Length; k++) {
			var (srcIdx, offset) = planted[k];
			videos.Add(Video($@"C:\clip{k}.mp4", ClipOf(sources[srcIdx].AudioFingerprint!, offset, ClipBlocks)));
			expected.Add((srcIdx, sources.Count + k, offset));
		}

		var engine = new ScanEngine();
		engine.Settings.PartialClipMinRatio = 0.10;
		engine.Settings.MaxDegreeOfParallelism = 4;

		var matches = new ConcurrentBag<(int sourceIdx, int clipIdx, float sim, int offsetSec)>();
		engine.BuildPartialClipMatchesIndexed(videos, simThreshold, matches);
		var found = matches.OrderBy(m => m.clipIdx).ToList();

		// Every planted clip is found, at its exact offset, with a high similarity...
		foreach (var (src, clip, offset) in expected) {
			var m = found.SingleOrDefault(x => x.clipIdx == clip);
			Assert.Equal(src, m.sourceIdx);
			Assert.Equal(offset, m.offsetSec);
			Assert.True(m.sim >= 0.99f, $"clip {clip}: sim {m.sim}");
		}
		// ...and nothing else is: unrelated random fingerprints must not match.
		Assert.Equal(expected.Count, found.Count);

		// The index must return exactly the pairs the brute oracle returns (no recall loss, no extras).
		var brutePairs = BrutePairs(videos, engine.Settings, simThreshold).OrderBy(p => p.clip).ToList();
		Assert.Equal(brutePairs, found.Select(m => (src: m.sourceIdx, clip: m.clipIdx)).ToList());
	}

	[Fact]
	public void IndexedMatcher_IgnoresClipsOutsideTheRatioGates() {
		var rnd = new Random(7);
		var source = RandomFingerprint(rnd, SourceBlocks);
		var videos = new List<FileEntry> {
			Video(@"C:\src.mp4", source),
			Video(@"C:\almost-whole.mp4", ClipOf(source, 0, 580)),   // 580/600 = 0.97 → visual dup, not a clip
			Video(@"C:\sliver.mp4", ClipOf(source, 100, 30)),        // 30/600 = 0.05 → below PartialClipMinRatio
		};

		var engine = new ScanEngine();
		engine.Settings.PartialClipMinRatio = 0.10;
		engine.Settings.MaxDegreeOfParallelism = 2;

		var matches = new ConcurrentBag<(int sourceIdx, int clipIdx, float sim, int offsetSec)>();
		engine.BuildPartialClipMatchesIndexed(videos, 0.80f, matches);

		Assert.Empty(matches);
	}
}
