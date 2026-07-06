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

// SeekSecondsForSample maps a container-based sample position onto the real video timeline so a
// container inflated by a long audio track / cover art doesn't seek past the video end. The stored
// grayBytes KEY is unchanged (compare compatibility); only the decode seek target moves.
public class VideoDurationMappingTests {
	static FileEntry Video(double containerSeconds, double videoSeconds) => new() {
		mediaInfo = new MediaInfo {
			Streams = System.Array.Empty<MediaInfo.StreamInfo>(),
			Duration = System.TimeSpan.FromSeconds(containerSeconds),
			VideoDurationSeconds = videoSeconds,
		}
	};

	[Fact]
	public void InflatedContainer_MapsSeekIntoVideo() {
		// JUL-733: container 6897s (audio/cover), video 837s. Key 1379.4 (0.2*6897) must seek to
		// 1379.4 * 837/6897 = 167.4s — inside the 837s video instead of ~542s past its end.
		var e = Video(6897, 837);
		Assert.Equal(167.4, e.SeekSecondsForSample(1379.4), 1);   // 0.2*6897 -> 0.2*837
		Assert.Equal(669.6, e.SeekSecondsForSample(5517.6), 1);   // 0.8*6897 -> 0.8*837, within video
	}

	[Fact]
	public void NormalFile_VideoEqualsContainer_Unchanged() {
		var e = Video(3600, 3600);
		Assert.Equal(1800, e.SeekSecondsForSample(1800), 3);
	}

	[Fact]
	public void UnknownVideoDuration_Unchanged() {
		// Pre-fix cached probe (VideoDurationSeconds == 0) or no per-stream duration: leave as-is.
		var e = Video(3600, 0);
		Assert.Equal(1800, e.SeekSecondsForSample(1800), 3);
	}

	[Fact]
	public void VideoNotShorterThanContainer_Unchanged() {
		var e = Video(1000, 2000);   // guard against a bogus longer stream duration
		Assert.Equal(500, e.SeekSecondsForSample(500), 3);
	}

	[Fact]
	public void NoMediaInfo_Unchanged() {
		Assert.Equal(500, new FileEntry().SeekSecondsForSample(500), 3);
	}

	[Fact]
	public void Reader_ExtractsVideoStreamDuration_ExcludingCoverArt() {
		// Real-world shape (JUL-733): container/audio 6897s, real h264 video 837s, plus an attached
		// mjpeg cover spanning the whole file. Durations are sexagesimal (VDF probes with -sexagesimal).
		// The reader must pick the h264 837s, not the 6897s container/cover.
		string json = @"{""streams"":[
			{""index"":0,""codec_type"":""video"",""codec_name"":""h264"",""duration"":""0:13:57.098256"",""disposition"":{""attached_pic"":0}},
			{""index"":1,""codec_type"":""audio"",""codec_name"":""aac"",""duration"":""1:54:57.063917""},
			{""index"":2,""codec_type"":""video"",""codec_name"":""mjpeg"",""duration"":""1:54:57.063922"",""disposition"":{""attached_pic"":1}}
		],""format"":{""duration"":""1:54:57.063917""}}";
		var info = VDF.Core.FFTools.FFProbeJsonReader.Read(System.Text.Encoding.UTF8.GetBytes(json), "test.mp4");
		Assert.Equal(6897, info.Duration.TotalSeconds, 0);      // container (truncated to seconds)
		Assert.Equal(837.1, info.VideoDurationSeconds, 1);      // real video stream, cover art excluded
	}
}
