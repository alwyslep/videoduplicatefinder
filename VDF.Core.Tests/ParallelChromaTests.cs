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
// Golden tests for segment-parallel fingerprinting (PARALLEL-AUDIO-DECODE-DESIGN.md):
// the segmented FrameFingerprinter pipeline with warmup lead-ins and shadow tails,
// merged by FrameFingerprinter.AggregateFrames, must be bit-identical to the
// sequential ChromaContext on the same PCM.  These tests are hermetic — they cover
// the chroma stage (hop alignment, FIR warmup, bucket close/flush, majority vote,
// seam shadow compare); the decode/resample stage is verified at runtime by the
// per-file seam check in ParallelAudioFingerprinter.

using VDF.Core.Chromaprint;
using VDF.Core.Chromaprint.Pipeline;

namespace VDF.Core.Tests;

public class ParallelChromaTests {
	private const int SampleRate = 11025;
	private const int FrameHop = ChromaContext.FrameHop; // 1365
	private const int ShadowFrames = 16;
	private const int WarmupSamples = SampleRate;        // 1 s, mirrors production
	private const int ShadowSamples = 3 * SampleRate;    // 3 s, mirrors production

	/// <summary>Deterministic synthetic PCM: sine mixture + LCG noise (no Random/Date).</summary>
	private static short[] GeneratePcm(int totalSamples, uint seed) {
		var pcm = new short[totalSamples];
		uint lcg = seed;
		for (int i = 0; i < totalSamples; i++) {
			double t = (double)i / SampleRate;
			// Slowly sweeping tones so chroma bins vary over time
			double v = 0.35 * Math.Sin(2 * Math.PI * (220 + 40 * Math.Sin(t / 7.0)) * t)
					 + 0.25 * Math.Sin(2 * Math.PI * (523.25 + 90 * Math.Sin(t / 11.0)) * t)
					 + 0.15 * Math.Sin(2 * Math.PI * 987.77 * t);
			lcg = lcg * 1664525u + 1013904223u;
			v += ((int)(lcg >> 16) % 2048 - 1024) / 32768.0 * 0.3;
			pcm[i] = (short)Math.Clamp((int)(v * 20000), short.MinValue, short.MaxValue);
		}
		return pcm;
	}

	private static uint[] SequentialReference(short[] pcm) {
		var ctx = new ChromaContext();
		ctx.Start();
		// Feed in odd-sized chunks to exercise the carry buffer
		int pos = 0;
		while (pos < pcm.Length) {
			int chunk = Math.Min(4099, pcm.Length - pos);
			ctx.Feed(pcm.AsSpan(pos, chunk));
			pos += chunk;
		}
		ctx.Finish();
		return ctx.GetRawFingerprint();
	}

	/// <summary>
	/// Emulates the parallel worker slicing exactly as ParallelAudioFingerprinter does:
	/// per segment a fresh FrameFingerprinter starting at the warmup offset, emit/shadow
	/// ranges on the global frame grid, seam verification, then AggregateFrames.
	/// </summary>
	private static uint[] SegmentedResult(short[] pcm, int segmentSamples) {
		int segmentCount = (pcm.Length + segmentSamples - 1) / segmentSamples;
		var emittedPerSegment = new List<(int Index, uint Fp)>[segmentCount];
		var shadowPerSegment = new List<(int Index, uint Fp)>[segmentCount];

		for (int k = 0; k < segmentCount; k++) {
			long emitStart = (long)k * segmentSamples;
			bool isLast = k == segmentCount - 1;
			long emitEnd = isLast ? long.MaxValue : (k + 1) * (long)segmentSamples;
			long warmupStart = Math.Max(0, emitStart - WarmupSamples);
			long feedEnd = isLast ? pcm.Length : Math.Min(pcm.Length, emitEnd + ShadowSamples);

			long emitFromFrame = (emitStart + FrameHop - 1) / FrameHop;
			long emitEndFrame = isLast ? long.MaxValue : (emitEnd + FrameHop - 1) / FrameHop;
			long shadowEndFrame = isLast ? long.MaxValue : emitEndFrame + ShadowFrames;

			var emitted = new List<(int, uint)>();
			var shadow = new List<(int, uint)>();
			var ff = new FrameFingerprinter(warmupStart);
			Assert.True(k == 0 || emitFromFrame >= ff.FirstEmittableFrame,
				"warmup must prime the FIR before the emit range");

			void OnFrame(int idx, uint fp) {
				if (idx >= emitFromFrame && idx < emitEndFrame) emitted.Add((idx, fp));
				else if (idx >= emitEndFrame && idx < shadowEndFrame) shadow.Add((idx, fp));
			}

			// Odd chunk sizes again, different from the reference chunking on purpose:
			// results must not depend on how the PCM is chunked.
			long pos = warmupStart;
			while (pos < feedEnd) {
				int chunk = (int)Math.Min(2731, feedEnd - pos);
				ff.Feed(pcm.AsSpan((int)pos, chunk), OnFrame);
				pos += chunk;
			}

			emittedPerSegment[k] = emitted;
			shadowPerSegment[k] = shadow;
		}

		// Seam verification — identical rule to production.
		for (int k = 0; k + 1 < segmentCount; k++) {
			var shadow = shadowPerSegment[k];
			var next = emittedPerSegment[k + 1];
			int checkCount = Math.Min(shadow.Count, Math.Min(next.Count, ShadowFrames));
			Assert.True(checkCount > 0, $"seam {k}: no overlap frames");
			for (int j = 0; j < checkCount; j++) {
				Assert.Equal(shadow[j].Index, next[j].Index);
				Assert.Equal(shadow[j].Fp, next[j].Fp);
			}
		}

		// Frame continuity across borders, then merge.
		var all = new List<(int Index, uint Fp)>();
		foreach (var emitted in emittedPerSegment) {
			if (emitted.Count == 0) continue;
			if (all.Count > 0)
				Assert.Equal(all[^1].Index + 1, emitted[0].Index);
			all.AddRange(emitted);
		}
		return FrameFingerprinter.AggregateFrames(all);
	}

	[Theory]
	[InlineData(100, 330750, 42u)]   // 100 s, 30 s segments, boundary NOT on the hop grid
	[InlineData(70, 220500, 7u)]     // 70 s, 20 s segments
	[InlineData(65, 273000, 1234u)]  // boundary exactly divisible by 1365 (grid-aligned seam)
	[InlineData(61, 660000, 99u)]    // last segment much shorter than the first
	public void SegmentedPipeline_MatchesSequential_BitExact(int seconds, int segmentSamples, uint seed) {
		var pcm = GeneratePcm(seconds * SampleRate, seed);
		uint[] reference = SequentialReference(pcm);
		uint[] parallel = SegmentedResult(pcm, segmentSamples);
		Assert.Equal(reference, parallel);
	}

	[Fact]
	public void SegmentedPipeline_TailNotOnSecondBoundary_MatchesSequential() {
		// 66.7 s: the final 1-second bucket is partial, exercising the flush path.
		var pcm = GeneratePcm((int)(66.7 * SampleRate), 4242u);
		Assert.Equal(SequentialReference(pcm), SegmentedResult(pcm, 30 * SampleRate));
	}

	[Fact]
	public void FrameFingerprinter_FromZero_MatchesChromaContextFrameForFrame() {
		// Worker 0 must reproduce the sequential stream exactly, including the
		// 4-frame FIR priming offset (first emitted frame index is 4).
		var pcm = GeneratePcm(10 * SampleRate, 5u);
		var frames = new List<(int Index, uint Fp)>();
		var ff = new FrameFingerprinter(0);
		ff.Feed(pcm, (idx, fp) => frames.Add((idx, fp)));

		Assert.NotEmpty(frames);
		Assert.Equal(4, frames[0].Index);
		Assert.Equal(SequentialReference(pcm), FrameFingerprinter.AggregateFrames(frames));
	}

	[Fact]
	public void AggregateFrames_EmptyInput_ReturnsEmpty() {
		Assert.Empty(FrameFingerprinter.AggregateFrames(new List<(int, uint)>()));
	}
}
