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

using System.Buffers.Binary;

namespace VDF.Core.Utils {
	// Container-invariant content fingerprint for MP4-family files. Where oshash hashes the WHOLE file
	// (so a metadata/moov edit — a tag write, cover embed, or faststart relocation — changes it even
	// though the media streams are untouched), this hashes only the `mdat` (media sample data) PAYLOAD:
	// value = mdat payload length + 64-bit LE checksum of its first and last 64 KiB. A metadata change
	// leaves mdat byte-identical, so the hash is stable across it — letting a rescan relink such a file
	// and reuse its (still-valid, stream-based) frame/audio analysis instead of re-decoding. Same cost
	// profile as oshash (~128 KiB I/O + a few KiB of header parse). See MDAT-HASH-DESIGN.md.
	//
	// MP4-only by design: returns null for anything it cannot parse as a single-`mdat` ISO-BMFF file
	// (non-MP4, multiple/absent mdat, malformed, or IO error). Callers treat null as "not mdat-hashable"
	// and fall back to whole-file oshash — never a wrong match.
	public static class MdatHashUtils {
		const int ChunkSize = 64 * 1024;
		const int MaxBoxes = 4096;   // sanity cap on the top-level box walk

		public static string? TryCompute(string path) {
			try {
				using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
				long fileLen = fs.Length;
				if (fileLen < 16)
					return null;

				long mdatDataOff = -1, mdatDataLen = -1;
				long off = 0;
				int boxes = 0;
				bool sawFtyp = false;
				Span<byte> hdr = stackalloc byte[16];

				// Walk top-level boxes: [uint32 size][4-char type], with size==1 => 64-bit largesize
				// follows, size==0 => box extends to EOF. Find the single mdat and remember its payload.
				while (off + 8 <= fileLen && boxes++ < MaxBoxes) {
					fs.Seek(off, SeekOrigin.Begin);
					fs.ReadExactly(hdr.Slice(0, 8));
					uint size32 = BinaryPrimitives.ReadUInt32BigEndian(hdr.Slice(0, 4));
					long boxSize;
					int hdrLen;
					if (size32 == 1) {                        // 64-bit largesize (>4 GiB mdat)
						if (off + 16 > fileLen) return null;
						fs.ReadExactly(hdr.Slice(8, 8));
						boxSize = (long)BinaryPrimitives.ReadUInt64BigEndian(hdr.Slice(8, 8));
						hdrLen = 16;
					}
					else if (size32 == 0) {                   // extends to end of file
						boxSize = fileLen - off;
						hdrLen = 8;
					}
					else {
						boxSize = size32;
						hdrLen = 8;
					}
					if (boxSize < hdrLen || off + boxSize > fileLen)
						return null;                          // malformed / not really a box stream

					// The first top-level box of any MP4 is `ftyp`; require it as the format signature so
					// arbitrary binaries can't accidentally walk as boxes.
					if (off == 0) {
						if (!(hdr[4] == (byte)'f' && hdr[5] == (byte)'t' && hdr[6] == (byte)'y' && hdr[7] == (byte)'p'))
							return null;
						sawFtyp = true;
					}
					if (hdr[4] == (byte)'m' && hdr[5] == (byte)'d' && hdr[6] == (byte)'a' && hdr[7] == (byte)'t') {
						if (mdatDataOff >= 0)
							return null;                      // more than one mdat -> ambiguous, don't guess
						mdatDataOff = off + hdrLen;
						mdatDataLen = boxSize - hdrLen;
					}
					off += boxSize;
				}

				if (!sawFtyp || mdatDataOff < 0 || mdatDataLen < ChunkSize)
					return null;

				Span<byte> buf = stackalloc byte[ChunkSize];
				ulong sum = (ulong)mdatDataLen;

				fs.Seek(mdatDataOff, SeekOrigin.Begin);                         // first 64 KiB of mdat payload
				fs.ReadExactly(buf);
				sum += SumLittleEndianU64(buf);

				fs.Seek(mdatDataOff + mdatDataLen - ChunkSize, SeekOrigin.Begin); // last 64 KiB of mdat payload
				fs.ReadExactly(buf);
				sum += SumLittleEndianU64(buf);

				return sum.ToString("x16");
			}
			catch {
				return null;
			}
		}

		static ulong SumLittleEndianU64(ReadOnlySpan<byte> buf) {
			ulong sum = 0;
			for (int i = 0; i + 8 <= buf.Length; i += 8)
				sum += BinaryPrimitives.ReadUInt64LittleEndian(buf.Slice(i, 8));
			return sum;
		}
	}
}
