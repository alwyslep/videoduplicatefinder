# Segment-Parallel Audio Fingerprint Decode — Design

Status: implementing (2026-07-05). Hybrid: user selects between the existing
sequential path and the new parallel path; unsupported files fall back per-file.

## Motivation (measured, 2026-07-05, F:/G: USB HDDs, 1 worker each)

- Per-file wall time: F ≈ 43 s, G ≈ 35 s. Disk ~68% busy, CPU ~0.8 core/worker.
- The single worker alternates read↔decode; neither resource saturates.
- Fingerprinting reads ~the whole file (interleaved mp4 + OS readahead), so the
  floor is the sequential read time (~25 s for 3.2 GB). Decode CPU is ~34
  core-seconds/file — parallelizing it collapses CPU wall time to ~2 s and the
  file becomes read-bound: 43 s → ~28 s (F), 35 s → ~21 s (G).

## Architecture (per file)

```
reader thread (sequential av_read_frame, AVDISCARD_ALL on non-audio)
  └─ clones audio packets → segment buckets (RAM, audio-only ≈ 2 MB/min)
       └─ segment ready → decode task (global SemaphoreSlim cap)
            worker: fresh AVCodecContext + SwrContext
              decode [warmup | emit | shadow] packet range
              → per-frame 32-bit fps tagged with global frame index
       └─ reducer: seam shadow-compare → bucket by second → majority vote
```

- Disk: exactly one sequential reader — the HDD never seeks for parallelism.
- RAM: only audio packets of in-flight segments (decode outruns read, so the
  queue stays near-empty; hard cap 512 MB stalls the reader as a backstop).
- CPU: decode workers pulled from a process-wide pool capped by
  `Settings.ParallelAudioDecodeThreads` (shared across drives/files).

## Bit-identity at seams

Every pipeline stage has bounded lookback, so a worker that starts early
(warmup) and discards pre-segment output converges to the exact sequential
state before its first emitted sample:

| stage             | state depth                  | seam handling                |
|-------------------|------------------------------|------------------------------|
| AAC decode        | 1 frame (MDCT overlap-add)   | warmup ≥ 1 packet            |
| swr resample      | FIR history + *phase*        | boundaries on phase-0 cycles |
| chroma FIR filter | 4 frames                     | warmup ≥ 4 frames            |
| frame hop (1365)  | global sample index mod 1365 | global frame indexing        |
| 1-s buckets       | none (pure function of idx)  | reducer replicates exactly   |

Resampler phase: output grid of srcRate→11025 repeats every
`C = srcRate / gcd(srcRate, 11025)` input samples (44100: 4; 48000: 640).
Segment/warmup boundaries are restricted to packet indices where
`pktIdx × samplesPerPacket ≡ 0 (mod C)` — a fresh SwrContext then produces the
identical output grid. For AAC (1024) at 48 kHz that is every 5th packet.

Warmup = 1 s before the emit range; shadow = 2 s past it. Frame k needs output
samples [k·1365, k·1365+4096), so warmup also covers the 4 priming frames and
shadow covers frames straddling the boundary.

## Seam verification (converts silent corruption into detected fallback)

Worker k keeps decoding into worker k+1's territory and emits the first
SHADOW_FRAMES (16 ≈ 2 s) of it as *shadow* fps from its long-running decoder
state. The reducer requires shadow(k) == real(k+1) frame-for-frame.

Induction: worker 0 starts at packet 0 (true sequential prefix). A matching
seam proves worker k+1's fresh-decoder state converged to the continuous state
inside the overlap window; decoder/swr/FIR state depth < window, so equality
during the window implies equality after it. All seams match ⇒ parallel result
== sequential result. Any mismatch ⇒ discard, re-run the file sequentially.

## Supported profile (else: sequential path, unchanged)

- native FFmpeg binding available; parallel threads setting > 1
- known duration ≥ 60 s; known sample rate; mono/stereo/any layout
- constant samples-per-packet (checked on every packet duration; AAC-LC = 1024)
- any deviation mid-read, decode error, or seam mismatch → per-file fallback

Corrupted packets: the sequential path skips them (`continue`), shifting all
later sample offsets — undetectable a priori, caught by seam checks or
send_packet errors → fallback preserves today's behaviour exactly.

## Hybrid selection

`Settings.ParallelAudioDecodeThreads` (int): 0/1 = existing sequential path
(default, ships off); N>1 = parallel with N-thread global decode cap.
GUI: checkbox + thread NumericUpDown in the Partial Clip Detection group.
Existing sequential code is untouched; the parallel path is a pre-route in
`ChromaprintEngine.ExtractFingerprint` that can only fall through, never alter
sequential behaviour.

## Test plan

1. **Hermetic golden test** (no ffmpeg needed): synthetic PCM (sine sweep +
   deterministic PRNG noise) → `ChromaContext` (reference) vs segmented
   `FrameFingerprinter` + reducer with warmup/shadow across odd boundaries →
   assert `uint[]` bit-equality. Covers hop alignment, FIR warmup, bucket
   close/flush, majority vote, shadow compare.
2. **Runtime seam check** on every parallel file (above) — the production
   guarantee; mismatches logged `[ParallelFp] seam mismatch → sequential`.
3. **Manual A/B**: scan a local folder twice (setting 0 vs 20), diff the
   database fingerprints; plus spot-check partial-clip results unchanged.
