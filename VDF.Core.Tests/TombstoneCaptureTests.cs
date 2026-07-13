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

// Delete-time capture ingest: a captured snapshot must land in the DB exactly once —
// content already known by path or OsHash is skipped, junk blobs are rejected. Getting the
// dedupe wrong would either resurrect purged fingerprints or double-count live content.
public class TombstoneCaptureTests {
	static FileEntry MakeEntry(string path, string? oshash) {
		var entry = new FileEntry {
			Path = path,
			mediaInfo = new MediaInfo { Duration = TimeSpan.FromSeconds(100) },
			OsHash = oshash,
			FileSize = 12345,
		};
		entry.grayBytes[20d] = new byte[] { 1, 2, 3 };
		entry.grayBytes[40d] = new byte[] { 4, 5, 6 };
		return entry;
	}

	[Fact]
	public void CapturedEntry_RoundTrips_IntoDatabase_Once() {
		string path = @"C:\__vdf_capture_test__\roundtrip.mp4";
		var blob = TombstoneCapture.Serialize(MakeEntry(path, "cafebabe00000001"));
		var known = new HashSet<string>(StringComparer.Ordinal);
		var probe = new FileEntry { Path = path };
		try {
			Assert.Equal(TombstoneCapture.IngestResult.Added, TombstoneCapture.TryIngestCapturedEntry(blob, known));
			Assert.True(DatabaseUtils.Database.TryGetValue(probe, out var stored));
			Assert.Equal("cafebabe00000001", stored!.OsHash);
			Assert.Equal(2, stored.grayBytes.Count);
			Assert.Equal(new byte[] { 1, 2, 3 }, stored.grayBytes[20d]);
			// Same path again -> path-known skip.
			Assert.Equal(TombstoneCapture.IngestResult.SkippedPathKnown, TombstoneCapture.TryIngestCapturedEntry(blob, known));
			// Same content at another path -> content-known skip.
			var twin = TombstoneCapture.Serialize(MakeEntry(@"C:\__vdf_capture_test__\twin.mp4", "cafebabe00000001"));
			Assert.Equal(TombstoneCapture.IngestResult.SkippedContentKnown, TombstoneCapture.TryIngestCapturedEntry(twin, known));
		}
		finally {
			DatabaseUtils.Database.Remove(probe);
		}
	}

	[Fact]
	public void JunkOrIncompleteBlobs_AreInvalid_AndNeverAdded() {
		var known = new HashSet<string>(StringComparer.Ordinal);
		Assert.Equal(TombstoneCapture.IngestResult.Invalid,
			TombstoneCapture.TryIngestCapturedEntry(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, known));

		// Structurally valid FileEntry but no fingerprint -> useless as a tombstone.
		var bare = new FileEntry { Path = @"C:\__vdf_capture_test__\bare.mp4", mediaInfo = new MediaInfo { Duration = TimeSpan.FromSeconds(5) } };
		Assert.Equal(TombstoneCapture.IngestResult.Invalid,
			TombstoneCapture.TryIngestCapturedEntry(TombstoneCapture.Serialize(bare), known));
		Assert.DoesNotContain(new FileEntry { Path = @"C:\__vdf_capture_test__\bare.mp4" }, DatabaseUtils.Database);
	}

	[Fact]
	public void SamplePositions_MatchScanFormula() {
		var p = TombstoneCapture.SamplePositions(4);
		Assert.Equal(4, p.Count);
		// (i+1)/(N+1), accumulated in float exactly like BuildFileList does.
		float acc = 0f;
		for (int i = 0; i < 4; i++) {
			acc += 1.0F / 5;
			Assert.Equal(acc, p[i]);
		}
	}
}
