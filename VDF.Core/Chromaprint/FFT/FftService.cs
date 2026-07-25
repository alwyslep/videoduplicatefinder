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
// Derived from AcoustID.NET by wo80 (https://github.com/wo80/AcoustID.NET), LGPL 2.1.
// Retargeted to net9.0; replaced LomontFFT with an in-place radix-2 Cooley-Tukey FFT.

namespace VDF.Core.Chromaprint.FFT {

	/// <summary>
	/// In-place radix-2 DIT Cooley-Tukey FFT operating on a power-of-two length array.
	/// Input length must be a power of two.
	/// After <see cref="Forward"/>, re[k] and im[k] hold the real and imaginary parts
	/// of the k-th frequency bin (not normalized).
	/// </summary>
	internal static class FftService {
		/// <summary>
		/// Compute the forward DFT in-place.
		/// </summary>
		/// <remarks>
		/// PERFORMANCE / BIT-EXACTNESS CONTRACT — read before touching the arithmetic.
		///
		/// This FFT is ~all of the managed fingerprint cost (a 4096-point transform per 1365-sample
		/// hop ⇒ ~24.6k butterflies per frame, ~290M per 24 minutes of audio; measured ≈3 core-s per
		/// audio-hour, 2026-07-25). It is therefore worth optimising — but its output feeds the stored
		/// audio fingerprint, and the database holds ~29k already-fingerprinted files. Any change that
		/// alters the floating-point RESULT (not just the speed) makes new fingerprints incomparable
		/// with the stored ones, i.e. silently breaks partial-clip detection across the library.
		///
		/// So the only admissible optimisations are ones that keep every operation's operands and
		/// order identical per element:
		///   • pointer access instead of bounds-checked indexing (done below)
		///   • hoisting repeated address arithmetic (done below)
		/// The following are NOT admissible without a fingerprint-version bump:
		///   • precomputed twiddle tables (differ in the last bits from the running rotation)
		///   • real-input FFT via an N/2 complex transform (different operation graph, ~2× faster)
		///   • float instead of double, or FMA contraction
		/// </remarks>
		internal static unsafe void Forward(double[] re, double[] im) {
			int n = re.Length;

			// Bit-reversal permutation
			for (int i = 1, j = 0; i < n; i++) {
				int bit = n >> 1;
				for (; (j & bit) != 0; bit >>= 1)
					j ^= bit;
				j ^= bit;
				if (i < j) {
					(re[i], re[j]) = (re[j], re[i]);
					(im[i], im[j]) = (im[j], im[i]);
				}
			}

			// Butterfly stages. Same operations, same order, same operands as the indexed version —
			// only the bounds checks and the repeated `i + j (+ half)` address math are gone.
			fixed (double* pRe = re, pIm = im) {
				for (int len = 2; len <= n; len <<= 1) {
					double ang = -2.0 * Math.PI / len;
					double wRe = Math.Cos(ang);
					double wIm = Math.Sin(ang);
					int half = len >> 1;
					for (int i = 0; i < n; i += len) {
						double curRe = 1.0, curIm = 0.0;
						double* aRe = pRe + i, aIm = pIm + i;          // u side
						double* bRe = aRe + half, bIm = aIm + half;    // v side
						for (int j = 0; j < half; j++) {
							double uRe = aRe[j];
							double uIm = aIm[j];
							double xRe = bRe[j];
							double xIm = bIm[j];
							double vRe = xRe * curRe - xIm * curIm;
							double vIm = xRe * curIm + xIm * curRe;
							aRe[j] = uRe + vRe;
							aIm[j] = uIm + vIm;
							bRe[j] = uRe - vRe;
							bIm[j] = uIm - vIm;
							double tmpRe = curRe * wRe - curIm * wIm;
							curIm = curRe * wIm + curIm * wRe;
							curRe = tmpRe;
						}
					}
				}
			}
		}
	}
}
