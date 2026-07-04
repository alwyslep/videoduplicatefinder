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

using System.Diagnostics;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using VDF.Core.Chromaprint;
using VDF.Core.Chromaprint.Pipeline;
using VDF.Core.Utils;

namespace VDF.Core.FFTools.FFmpegNative {

	/// <summary>
	/// Segment-parallel audio fingerprint extraction (see PARALLEL-AUDIO-DECODE-DESIGN.md).
	///
	/// One sequential reader walks the file exactly like the sequential path (the disk
	/// never seeks for parallelism) and clones audio packets into RAM; decode workers —
	/// capped by a process-wide gate — each decode one ~30 s segment with a warmup
	/// lead-in and a shadow tail, emitting per-frame fingerprints on the global frame
	/// grid.  Segment boundaries are restricted to resampler phase-zero packet indices
	/// so a fresh SwrContext reproduces the sequential output grid.
	///
	/// Every seam is verified: worker k's shadow frames (decoded with its long-running
	/// state) must equal worker k+1's first emitted frames (decoded from a fresh,
	/// warmed-up decoder).  By induction from worker 0 (which starts at the true stream
	/// start) matching seams prove the merged result is bit-identical to the sequential
	/// pipeline.  Any mismatch or unsupported profile falls back to the unchanged
	/// sequential path — this class can only ever *fail towards* it.
	/// </summary>
	internal static class ParallelAudioFingerprinter {
		private const int TargetRate = 11025;
		private const int SegmentSeconds = 30;
		private const double WarmupSeconds = 1.0;   // covers AAC overlap, swr FIR history, chroma FIR priming
		private const double ShadowSeconds = 3.0;   // covers ShadowFrames plus the FrameSize lookahead
		private const int ShadowFrames = 16;        // ≈ 2 s of frames cross-checked at every seam
		private const int MinDurationSeconds = 60;  // shorter files: sequential is fine
		private const long QueuedBytesCap = 512L * 1024 * 1024;        // per file
		private const long GlobalQueuedBytesCap = 1024L * 1024 * 1024; // all files together
		private const long PerFileFloorBytes = 32L * 1024 * 1024;      // a reader below this never stalls on the global cap
		private const int ReadTimeoutMs = 120_000;
		private const int MaxFramesPerPacket = 10_000;

		private static int maxDecodeThreads;
		private static SemaphoreSlim decodeGate = new(1, 1);
		private static long totalQueuedBytes; // process-wide cloned-packet RAM across concurrent files

		/// <summary>
		/// Process-wide decode-thread cap, shared by all drives/files. 0 or 1 disables
		/// the parallel path.  Set at scan start only — never while fingerprints are
		/// being extracted (the gate is swapped, not resized).
		/// </summary>
		internal static int MaxDecodeThreads {
			get => maxDecodeThreads;
			set {
				value = Math.Clamp(value, 0, 64);
				if (value == maxDecodeThreads) return;
				maxDecodeThreads = value;
				if (value > 1)
					decodeGate = new SemaphoreSlim(value, value);
			}
		}

		internal static bool Enabled => maxDecodeThreads > 1;

		private sealed class Segment {
			public int Index;
			public bool IsLast;
			public long DecodeStartPkt;
			public long EmitStartPkt;
			public long EmitEndPkt;
			public readonly List<IntPtr> Packets = new();
			public long PacketBytes;
			public Task<WorkerResult?>? Work;

			public unsafe void FreePackets() {
				foreach (var p in Packets) {
					AVPacket* pkt = (AVPacket*)p;
					ffmpeg.av_packet_free(&pkt);
				}
				Packets.Clear();
			}
		}

		private sealed class WorkerResult {
			public readonly List<(int Index, uint Fp)> Emitted = new();
			public readonly List<(int Index, uint Fp)> Shadow = new();
		}

		/// <summary>
		/// Attempts segment-parallel extraction.  Handled=false means the file does not
		/// fit the supported profile (or a seam mismatch was detected) and the caller
		/// must run the sequential path.  Handled=true returns the fingerprint —
		/// empty array for "no audio stream", null only on cancellation
		/// (matching the sequential contract).
		/// </summary>
		internal static unsafe (bool Handled, uint[]? Fingerprint) TryExtract(string filePath, bool extendedLogging, CancellationToken ct, Action<double>? onProgress) {
			if (!Enabled) return (false, null);

			var sw = extendedLogging ? Stopwatch.StartNew() : null;
			AVFormatContext* fmt = null;
			AVCodecParameters* parCopy = null;
			AVPacket* pkt = null;
			var segments = new List<Segment>();
			long queuedBytes = 0;
			Action<long> onBytesFreed = v => {
				Interlocked.Add(ref queuedBytes, -v);
				Interlocked.Add(ref totalQueuedBytes, -v);
			};
			string fileName = Path.GetFileName(filePath);
			int prevLogLevel = int.MinValue;

			// Interrupt callback state — same per-operation deadline scheme as AudioStreamDecoder.
			long timeoutTicks = (long)(ReadTimeoutMs / 1000.0 * Stopwatch.Frequency);
			long deadlineTicks = Stopwatch.GetTimestamp() + timeoutTicks;
			AVIOInterruptCB_callback interruptCb = _ =>
				(ct.IsCancellationRequested || Stopwatch.GetTimestamp() > Volatile.Read(ref deadlineTicks)) ? 1 : 0;

			(bool, uint[]?) Fallback(string reason) {
				if (reason.Length > 0)
					Logger.Instance.Info($"[ParallelFp] {fileName}: {reason} -> sequential path");
				return (false, null);
			}

			try {
				// Same noisy-warning suppression as the sequential native path — without it
				// the reader plus N workers flood stderr at FFmpeg's default INFO level.
				prevLogLevel = ffmpeg.av_log_get_level();
				ffmpeg.av_log_set_level(extendedLogging ? ffmpeg.AV_LOG_ERROR : ffmpeg.AV_LOG_FATAL);

				fmt = ffmpeg.avformat_alloc_context();
				if (fmt == null) return Fallback("failed to allocate format context");
				fmt->interrupt_callback = new AVIOInterruptCB { callback = interruptCb };

				var fmtLocal = fmt;
				int openRet = ffmpeg.avformat_open_input(&fmtLocal, filePath, null, null);
				fmt = fmtLocal;
				if (openRet < 0) return Fallback($"open failed ({openRet})");
				if (ffmpeg.avformat_find_stream_info(fmt, null) < 0) return Fallback("find_stream_info failed");

				AVCodec* codec = null;
				int streamIdx = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, &codec, 0);
				if (streamIdx < 0)
					return (true, Array.Empty<uint>()); // no audio stream — same result as sequential

				var stream = fmt->streams[streamIdx];
				for (int i = 0; i < (int)fmt->nb_streams; i++)
					if (i != streamIdx)
						fmt->streams[i]->discard = AVDiscard.AVDISCARD_ALL;

				double durationSeconds = 0;
				if (fmt->duration > 0)
					durationSeconds = fmt->duration / (double)ffmpeg.AV_TIME_BASE;
				else if (stream->duration > 0)
					durationSeconds = stream->duration * (double)stream->time_base.num / stream->time_base.den;
				if (durationSeconds < MinDurationSeconds)
					return Fallback(string.Empty);

				int srcRate = stream->codecpar->sample_rate;
				if (srcRate <= 0) return Fallback("unknown sample rate");

				parCopy = ffmpeg.avcodec_parameters_alloc();
				if (parCopy == null || ffmpeg.avcodec_parameters_copy(parCopy, stream->codecpar) < 0)
					return Fallback("codecpar copy failed");
				IntPtr parPtr = (IntPtr)parCopy;

				// Resampler phase cycle: the srcRate -> 11025 output grid repeats every
				// `cycle` input samples; warmup/segment starts must land on multiples of it.
				long cycle = srcRate / Gcd(srcRate, TargetRate);

				pkt = ffmpeg.av_packet_alloc();
				if (pkt == null) return Fallback("packet alloc failed");

				long sp = 0;                 // samples per packet (constant profile)
				long step = 0, segPkts = 0, warmupPkts = 0, shadowPkts = 0;
				long pktIndex = 0;
				long shortPktIndex = -1;     // a shorter packet is tolerated only as the very last one
				int nextDispatch = 0;
				double expectedTotalSamples = durationSeconds * srcRate;
				int lastReportedPercent = -1;
				int tbNum = stream->time_base.num;
				int tbDen = stream->time_base.den;

				Segment GetSegment(int k) {
					while (segments.Count <= k) {
						int idx = segments.Count;
						segments.Add(new Segment {
							Index = idx,
							DecodeStartPkt = Math.Max(0, idx * segPkts - warmupPkts),
							EmitStartPkt = idx * segPkts,
							EmitEndPkt = (idx + 1) * segPkts,
						});
					}
					return segments[k];
				}

				void Dispatch(Segment seg, long spLocal, int rate) {
					seg.Work = StartWorker(seg, parPtr, rate, spLocal, ct, extendedLogging, fileName, onBytesFreed);
				}

				while (true) {
					if (ct.IsCancellationRequested) return (true, null);
					ffmpeg.av_packet_unref(pkt);
					int readRet = ffmpeg.av_read_frame(fmt, pkt);
					if (readRet == ffmpeg.AVERROR_EOF) break;
					if (readRet < 0) return Fallback($"read error ({readRet})");
					Volatile.Write(ref deadlineTicks, Stopwatch.GetTimestamp() + timeoutTicks);

					if (pkt->stream_index != streamIdx) continue;

					// Constant samples-per-packet profile check via packet duration.
					long dur = pkt->duration;
					if (dur <= 0) return Fallback("packet without duration");
					long durSamples = dur * tbNum * srcRate;
					if (durSamples % tbDen != 0) return Fallback("non-integral packet duration");
					long pktSamples = durSamples / tbDen;

					if (sp == 0) {
						sp = pktSamples;
						if (sp <= 0 || sp > 65536) return Fallback($"unsupported packet size ({sp})");
						step = cycle / Gcd(sp, cycle);
						segPkts = Math.Max(step, (long)(SegmentSeconds * (double)srcRate / sp) / step * step);
						warmupPkts = CeilToMultiple((long)Math.Ceiling(WarmupSeconds * srcRate / sp), step);
						shadowPkts = (long)Math.Ceiling(ShadowSeconds * srcRate / sp) + 1;
					}
					else if (shortPktIndex >= 0) {
						return Fallback("short packet mid-stream");
					}
					else if (pktSamples != sp) {
						if (pktSamples < sp)
							shortPktIndex = pktIndex;  // may be the final packet — verified at EOF
						else
							return Fallback($"variable packet size ({pktSamples} vs {sp})");
					}

					// Route the packet into every segment whose decode range covers it (≤ 3).
					int k0 = (int)(pktIndex / segPkts);
					for (int k = Math.Max(0, k0 - 1); k <= k0 + 1; k++) {
						long decodeStart = Math.Max(0, k * segPkts - warmupPkts);
						long decodeEnd = (k + 1) * segPkts + shadowPkts;
						if (pktIndex < decodeStart || pktIndex >= decodeEnd) continue;
						AVPacket* clone = ffmpeg.av_packet_clone(pkt);
						if (clone == null) return Fallback("packet clone failed");
						var seg = GetSegment(k);
						seg.Packets.Add((IntPtr)clone);
						seg.PacketBytes += clone->size;
						Interlocked.Add(ref queuedBytes, clone->size);
						Interlocked.Add(ref totalQueuedBytes, clone->size);
					}

					// Dispatch segments whose decode range the reader has fully passed.
					// (Such a segment can never be the file's last one: any packet at or
					// beyond its emit end has already materialised the next segment.)
					while (nextDispatch < segments.Count && pktIndex >= (nextDispatch + 1) * segPkts + shadowPkts)
						Dispatch(segments[nextDispatch++], sp, srcRate);

					// RAM backstop — decode normally outruns the disk, so this rarely trips.
					// Stall on the per-file cap, or on the global cap when this file holds a
					// non-trivial share. Progress guarantee: bytes only drain via dispatched
					// workers, so if everything this file holds is undispatched (the reader
					// itself is the only thing that could free them) fall back instead of
					// sleeping forever — e.g. one >512 MB segment of very-high-bitrate audio.
					while (true) {
						long own = Interlocked.Read(ref queuedBytes);
						long global = Interlocked.Read(ref totalQueuedBytes);
						bool stall = own > QueuedBytesCap ||
							(global > GlobalQueuedBytesCap && own > PerFileFloorBytes);
						if (!stall) break;
						if (ct.IsCancellationRequested) return (true, null);
						long undispatched = 0;
						for (int k = nextDispatch; k < segments.Count; k++)
							undispatched += segments[k].PacketBytes;
						if (undispatched >= own)
							return Fallback("segment bytes exceed RAM cap");
						Thread.Sleep(5);
					}

					pktIndex++;

					if (onProgress != null && expectedTotalSamples > 0) {
						int pct = Math.Clamp((int)(98.0 * pktIndex * sp / expectedTotalSamples), 0, 98);
						if (pct != lastReportedPercent) {
							lastReportedPercent = pct;
							onProgress(pct / 100.0);
						}
					}
				}

				if (sp == 0 || pktIndex == 0 || segments.Count == 0) return Fallback("no audio packets");
				if (shortPktIndex >= 0 && shortPktIndex != pktIndex - 1)
					return Fallback("short packet mid-stream");

				// EOF: a tail segment materialised only by the warmup lookahead — or left
				// without a single completable frame window — can never emit, which would
				// force a guaranteed seam-check fallback (double decode) for file lengths
				// near a segment boundary. Fold the tail into the previous segment: it is
				// provably undispatched here (dispatch needs pktIndex ≥ N·segPkts+shadowPkts,
				// which lies past EOF) and its decode range already holds every tail packet.
				var lastSeg = segments[^1];
				if (segments.Count >= 2 && nextDispatch < segments.Count - 1) {
					long emitFromFrame = CeilDiv(OutSample(lastSeg.EmitStartPkt, sp, srcRate), ChromaContext.FrameHop);
					long availableOut = OutSample(pktIndex, sp, srcRate);
					// Conservative margin (one extra hop) for flush residue / a short final packet;
					// over-folding is safe — the previous segment just decodes a slightly longer tail.
					if (availableOut < emitFromFrame * ChromaContext.FrameHop + Chroma.FrameSize + ChromaContext.FrameHop) {
						FreeSegment(lastSeg, onBytesFreed);
						segments.RemoveAt(segments.Count - 1);
						lastSeg = segments[^1];
					}
				}
				lastSeg.IsLast = true;
				lastSeg.EmitEndPkt = long.MaxValue;
				while (nextDispatch < segments.Count)
					Dispatch(segments[nextDispatch++], sp, srcRate);

				// The file handle is no longer needed — workers run from RAM.
				var fmtToClose = fmt;
				ffmpeg.avformat_close_input(&fmtToClose);
				fmt = null;

				var results = new WorkerResult?[segments.Count];
				for (int i = 0; i < segments.Count; i++)
					results[i] = segments[i].Work!.GetAwaiter().GetResult();

				if (ct.IsCancellationRequested) return (true, null);
				for (int i = 0; i < results.Length; i++)
					if (results[i] == null)
						return Fallback($"segment {i} unsupported/failed");

				// Seam verification: shadow(k) must equal the head of emitted(k+1).
				for (int i = 0; i + 1 < results.Length; i++) {
					var shadow = results[i]!.Shadow;
					var nextEmitted = results[i + 1]!.Emitted;
					int checkCount = Math.Min(shadow.Count, Math.Min(nextEmitted.Count, ShadowFrames));
					if (checkCount == 0)
						return Fallback($"seam {i}: no overlap frames to verify");
					for (int j = 0; j < checkCount; j++)
						if (shadow[j].Index != nextEmitted[j].Index || shadow[j].Fp != nextEmitted[j].Fp)
							return Fallback($"seam {i}: mismatch at frame {nextEmitted[j].Index}");
				}

				// Merge, verifying global frame continuity across segment borders.
				var all = new List<(int Index, uint Fp)>();
				foreach (var r in results) {
					var emitted = r!.Emitted;
					if (emitted.Count == 0) continue;
					if (all.Count > 0 && emitted[0].Index != all[^1].Index + 1)
						return Fallback($"frame gap at segment border ({all[^1].Index} -> {emitted[0].Index})");
					all.AddRange(emitted);
				}
				if (all.Count == 0) return Fallback("produced no frames");

				var fingerprint = FrameFingerprinter.AggregateFrames(all);

				if (extendedLogging)
					Logger.Instance.Info($"[ParallelFp] {fileName}: parallel ok, total={sw!.ElapsedMilliseconds}ms, " +
						$"segments={segments.Count}, frames={all.Count}, blocks={fingerprint.Length}, threads<={maxDecodeThreads}");

				return (true, fingerprint);
			}
			catch (OperationCanceledException) {
				return (true, null);
			}
			catch (Exception e) {
				Logger.Instance.Info($"[ParallelFp] {fileName}: {e.GetType().Name}: {e.Message} -> sequential path");
				return (false, null);
			}
			finally {
				if (pkt != null) {
					var p = pkt;
					ffmpeg.av_packet_free(&p);
				}
				if (fmt != null) {
					var f = fmt;
					ffmpeg.avformat_close_input(&f);
				}
				// Workers free their own packets; wait for any dispatched ones before the
				// sweep below, which only covers undispatched segments and abort paths.
				foreach (var seg in segments) {
					if (seg.Work != null) {
						try { seg.Work.GetAwaiter().GetResult(); } catch { }
					}
					FreeSegment(seg, onBytesFreed); // idempotent — keeps the global byte counter honest
				}
				if (parCopy != null) {
					var pc = parCopy;
					ffmpeg.avcodec_parameters_free(&pc);
				}
				if (prevLogLevel != int.MinValue)
					ffmpeg.av_log_set_level(prevLogLevel);
				GC.KeepAlive(interruptCb);
			}
		}

		/// <summary>
		/// Queues one segment decode behind the global gate.  The gate wait is async so
		/// hundreds of queued segments never pin thread-pool threads.
		/// </summary>
		private static Task<WorkerResult?> StartWorker(Segment seg, IntPtr par, int srcRate, long sp,
			CancellationToken ct, bool extendedLogging, string fileName, Action<long> onBytesFreed) {
			// Pin the gate instance: the MaxDecodeThreads setter swaps the static field,
			// and Wait/Release must always pair on the same semaphore.
			var gate = decodeGate;
			return Task.Run(async () => {
				try {
					await gate.WaitAsync(ct).ConfigureAwait(false);
				}
				catch (OperationCanceledException) {
					FreeSegment(seg, onBytesFreed);
					return null;
				}
				try {
					return DecodeSegment(seg, par, srcRate, sp, ct, extendedLogging, fileName);
				}
				finally {
					FreeSegment(seg, onBytesFreed);
					gate.Release();
				}
			});
		}

		/// <summary>
		/// Decodes one segment from its cloned packets (no file I/O).
		/// Returns null when the file must fall back to the sequential path.
		/// </summary>
		private static unsafe WorkerResult? DecodeSegment(Segment seg, IntPtr parPtr, int srcRate, long sp,
			CancellationToken ct, bool extendedLogging, string fileName) {

			AVCodecParameters* par = (AVCodecParameters*)parPtr;
			AVCodecContext* codecCtx = null;
			SwrContext* swr = null;
			AVFrame* frame = null;
			byte[] outBuf = new byte[8192];

			try {
				if (ct.IsCancellationRequested) return null;

				AVCodec* codec = ffmpeg.avcodec_find_decoder(par->codec_id);
				if (codec == null) return null;
				codecCtx = ffmpeg.avcodec_alloc_context3(codec);
				if (codecCtx == null) return null;
				if (ffmpeg.avcodec_parameters_to_context(codecCtx, par) < 0) return null;
				if (ffmpeg.avcodec_open2(codecCtx, codec, null) < 0) return null;

				// Same construction as the sequential AudioStreamDecoder: source params
				// from the freshly opened codec context, mono s16 @ 11025 out.
				var outLayout = new AVChannelLayout();
				ffmpeg.av_channel_layout_default(&outLayout, 1);
				var inLayout = codecCtx->ch_layout;
				SwrContext* swrLocal = null;
				if (ffmpeg.swr_alloc_set_opts2(&swrLocal,
						&outLayout, AVSampleFormat.AV_SAMPLE_FMT_S16, TargetRate,
						&inLayout, codecCtx->sample_fmt, codecCtx->sample_rate,
						0, null) < 0) return null;
				swr = swrLocal;
				if (ffmpeg.swr_init(swr) < 0) return null;

				frame = ffmpeg.av_frame_alloc();
				if (frame == null) return null;

				long globalStartOut = OutSample(seg.DecodeStartPkt, sp, srcRate);
				long emitFromFrame = CeilDiv(OutSample(seg.EmitStartPkt, sp, srcRate), ChromaContext.FrameHop);
				long emitEndFrame = seg.EmitEndPkt == long.MaxValue
					? long.MaxValue
					: CeilDiv(OutSample(seg.EmitEndPkt, sp, srcRate), ChromaContext.FrameHop);
				long shadowEndFrame = emitEndFrame == long.MaxValue ? long.MaxValue : emitEndFrame + ShadowFrames;

				var fingerprinter = new FrameFingerprinter(globalStartOut);
				// Warmup must fully prime the chroma FIR before the emit range begins.
				if (seg.Index > 0 && emitFromFrame < fingerprinter.FirstEmittableFrame)
					return null;

				var result = new WorkerResult();
				bool done = false;
				void OnFrame(int idx, uint fp) {
					if (idx >= emitFromFrame && idx < emitEndFrame) result.Emitted.Add((idx, fp));
					else if (idx >= emitEndFrame && idx < shadowEndFrame) result.Shadow.Add((idx, fp));
					else if (idx >= shadowEndFrame) done = true;
				}

				for (int i = 0; i < seg.Packets.Count && !done; i++) {
					if (ct.IsCancellationRequested) return null;
					AVPacket* p = (AVPacket*)seg.Packets[i];
					int sendRet = ffmpeg.avcodec_send_packet(codecCtx, p);
					if (sendRet < 0 && sendRet != ffmpeg.AVERROR(ffmpeg.EAGAIN)) {
						// Sequential skips corrupted packets, shifting all later sample
						// offsets — irreproducible here, so the whole file falls back.
						return null;
					}
					// A short decoded frame is tolerated only for the last packet of the
					// list (only the file's final packet can legitimately be short; if an
					// interior one decodes short anyway, the seam checks catch the shift).
					bool lastInList = i == seg.Packets.Count - 1;
					if (!DrainDecoder(codecCtx, frame, swr, fingerprinter, OnFrame, ref outBuf, srcRate, sp, lastInList, allowEof: false))
						return null;
				}

				if (seg.IsLast && !done && !ct.IsCancellationRequested) {
					// Flush decoder and resampler exactly like the sequential tail.
					ffmpeg.avcodec_send_packet(codecCtx, null);
					if (!DrainDecoder(codecCtx, frame, swr, fingerprinter, OnFrame, ref outBuf, srcRate, sp, allowShort: true, allowEof: true))
						return null;
					ConvertAndFeed(swr, null, 0, fingerprinter, OnFrame, ref outBuf);
				}

				if (ct.IsCancellationRequested) return null;
				return result;
			}
			catch (Exception e) {
				if (extendedLogging)
					Logger.Instance.Info($"[ParallelFp] {fileName} segment {seg.Index}: {e.GetType().Name}: {e.Message}");
				return null;
			}
			finally {
				if (frame != null) {
					var f = frame;
					ffmpeg.av_frame_free(&f);
				}
				if (swr != null) {
					var s = swr;
					ffmpeg.swr_free(&s);
				}
				if (codecCtx != null) {
					var c = codecCtx;
					ffmpeg.avcodec_free_context(&c);
				}
			}
		}

		/// <summary>Receives all pending frames, verifying the constant-frame-size profile.</summary>
		private static unsafe bool DrainDecoder(AVCodecContext* codecCtx, AVFrame* frame, SwrContext* swr,
			FrameFingerprinter fingerprinter, Action<int, uint> onFrame, ref byte[] outBuf, int srcRate, long sp,
			bool allowShort, bool allowEof) {

			for (int iter = 0; iter < MaxFramesPerPacket; iter++) {
				int recvRet = ffmpeg.avcodec_receive_frame(codecCtx, frame);
				if (recvRet == ffmpeg.AVERROR(ffmpeg.EAGAIN) || recvRet == ffmpeg.AVERROR_EOF)
					return true;
				if (recvRet < 0) return false;

				// Profile guards: decoder delay, SBR up-sampling, or gapless trimming all
				// break the packet-index -> sample-offset math; fall back when detected.
				if (frame->sample_rate != srcRate) return false;
				if (frame->nb_samples > sp) return false;
				if (frame->nb_samples < sp && !allowShort) return false;

				ConvertAndFeed(swr, frame->extended_data, frame->nb_samples, fingerprinter, onFrame, ref outBuf);
				ffmpeg.av_frame_unref(frame);
			}
			return allowEof; // exceeding MaxFramesPerPacket mid-stream is a broken codec
		}

		/// <summary>swr conversion identical to AudioStreamDecoder.ConvertAndDeliver.</summary>
		private static unsafe void ConvertAndFeed(SwrContext* swr, byte** inputData, int inputSamples,
			FrameFingerprinter fingerprinter, Action<int, uint> onFrame, ref byte[] outBuf) {

			int outSamples = ffmpeg.swr_get_out_samples(swr, inputSamples);
			if (outSamples <= 0) return;
			int requiredBytes = outSamples * 2;
			if (outBuf.Length < requiredBytes)
				outBuf = new byte[requiredBytes];

			int converted;
			fixed (byte* outPtr = outBuf) {
				byte* op = outPtr;
				converted = ffmpeg.swr_convert(swr, &op, outSamples, inputData, inputSamples);
			}
			if (converted <= 0) return;
			fingerprinter.Feed(MemoryMarshal.Cast<byte, short>(outBuf.AsSpan(0, converted * 2)), onFrame);
		}

		private static void FreeSegment(Segment seg, Action<long> onBytesFreed) {
			long bytes = seg.PacketBytes;
			seg.PacketBytes = 0;
			seg.FreePackets();
			if (bytes > 0) onBytesFreed(bytes);
		}

		/// <summary>Global output-sample offset (11025 Hz grid) of a phase-aligned packet index.</summary>
		private static long OutSample(long pktIndex, long sp, int srcRate)
			=> pktIndex * sp * TargetRate / srcRate;

		private static long CeilDiv(long a, long b) => (a + b - 1) / b;

		private static long CeilToMultiple(long value, long multiple)
			=> CeilDiv(value, multiple) * multiple;

		private static long Gcd(long a, long b) {
			while (b != 0) (a, b) = (b, a % b);
			return a;
		}
	}
}
