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

namespace VDF.Core.Chromaprint.Pipeline {

	/// <summary>
	/// Per-frame variant of <see cref="ChromaContext"/> for segment-parallel decoding:
	/// consumes mono 16-bit 11025 Hz PCM that begins at an arbitrary global sample
	/// offset and emits (globalFrameIndex, fingerprint) pairs instead of aggregating
	/// into 1-second buckets.  Frame k covers global output samples
	/// [k*FrameHop, k*FrameHop + FrameSize); emission starts once the 5-tap chroma
	/// FIR filter is primed, i.e. from the 5th processed frame — identical to the
	/// sequential pipeline, which never emits fingerprints for frames 0–3.
	/// Bucket aggregation is replicated exactly by <see cref="AggregateFrames"/>.
	/// </summary>
	internal sealed class FrameFingerprinter {
		private readonly Chroma _chroma = new();
		private readonly ChromaFilter _filter = new();
		private readonly double[] _chromaBuf = new double[12];
		private readonly double[] _filteredBuf = new double[12];
		private readonly double[] _frameBuf = new double[Chroma.FrameSize];   // see ChromaContext._frameBuf

		private short[] _samples = Array.Empty<short>();
		private int _sampleCount;
		private int _skipRemaining;  // leading samples dropped to land on the global hop grid
		private int _frameIndex;     // global index of the next frame to process

		/// <summary>Global frame index of the first frame this instance can emit (filter primed).</summary>
		internal int FirstEmittableFrame { get; }

		/// <param name="globalStartSample">
		/// Global output-sample offset (11025 Hz grid) of the first PCM sample that will
		/// be fed.  Frames are aligned to the global grid: processing begins at the first
		/// frame whose window starts at or after this offset.
		/// </param>
		internal FrameFingerprinter(long globalStartSample) {
			long firstFrame = (globalStartSample + ChromaContext.FrameHop - 1) / ChromaContext.FrameHop; // ceil
			_skipRemaining = (int)(firstFrame * ChromaContext.FrameHop - globalStartSample);
			_frameIndex = (int)firstFrame;
			FirstEmittableFrame = _frameIndex + ChromaFilterPrimingFrames;
		}

		// The 5-tap FIR needs 5 fed frames before its first valid output.
		internal const int ChromaFilterPrimingFrames = 4;

		/// <summary>
		/// Feeds PCM and invokes <paramref name="onFrame"/> for every completed frame
		/// once the FIR filter is primed.  Leftover samples are carried to the next call.
		/// </summary>
		internal void Feed(ReadOnlySpan<short> samples, Action<int, uint> onFrame) {
			if (_skipRemaining > 0) {
				int skip = Math.Min(_skipRemaining, samples.Length);
				samples = samples.Slice(skip);
				_skipRemaining -= skip;
				if (samples.IsEmpty) return;
			}

			int needed = _sampleCount + samples.Length;
			if (_samples.Length < needed) {
				var newBuf = new short[needed + Chroma.FrameSize];
				_samples.AsSpan(0, _sampleCount).CopyTo(newBuf);
				_samples = newBuf;
			}
			samples.CopyTo(_samples.AsSpan(_sampleCount));
			_sampleCount += samples.Length;

			Span<double> frameBuf = _frameBuf;
			int pos = 0;
			while (pos + Chroma.FrameSize <= _sampleCount) {
				for (int i = 0; i < Chroma.FrameSize; i++)
					frameBuf[i] = _samples[pos + i] * (1.0 / 32768.0);

				Array.Clear(_chromaBuf, 0, 12);
				_chroma.Compute(frameBuf, _chromaBuf);

				if (_filter.Feed(_chromaBuf, _filteredBuf)) {
					ChromaNormalizer.Normalize(_filteredBuf);
					onFrame(_frameIndex, FingerprintCalculator.Compute(_filteredBuf));
				}
				_frameIndex++;
				pos += ChromaContext.FrameHop;
			}

			int leftover = _sampleCount - pos;
			if (leftover > 0 && pos > 0)
				Array.Copy(_samples, pos, _samples, 0, leftover);
			_sampleCount = leftover;
		}

		/// <summary>
		/// Aggregates per-frame fingerprints (ascending global frame index, no gaps
		/// beyond the initial priming offset) into 1-second majority-vote blocks —
		/// the exact bucket-close/flush semantics of <see cref="ChromaContext"/>:
		/// a frame belongs to bucket floor(index*FrameHop/SampleRate), the open bucket
		/// closes when a frame from a later bucket arrives, and the final partial
		/// bucket is flushed at the end.
		/// </summary>
		internal static uint[] AggregateFrames(IEnumerable<(int Index, uint Fp)> frames) {
			var aggregated = new List<uint>();
			var secondFrames = new List<uint>(16);
			foreach (var (index, fp) in frames) {
				double frameSec = (double)index * ChromaContext.FrameHop / Chroma.SampleRate;
				double bucket = Math.Floor(frameSec);
				if (secondFrames.Count > 0 && bucket > aggregated.Count) {
					aggregated.Add(FingerprintCalculator.AggregateMajorityVote(secondFrames));
					secondFrames.Clear();
				}
				secondFrames.Add(fp);
			}
			if (secondFrames.Count > 0)
				aggregated.Add(FingerprintCalculator.AggregateMajorityVote(secondFrames));
			return aggregated.ToArray();
		}
	}
}
