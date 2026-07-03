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

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VDF.Core.Utils {
	// On-disk position (first-extent LCN) of a file, used to order each drive's gather queue so a
	// spindle drive's single head assembly sweeps the platters in one direction instead of doing
	// random full-stroke seeks between consecutive files. Measured on an 18TB exFAT USB drive:
	// name-order processing swept 23x more head travel than LCN order. Windows-only — elsewhere
	// GetFirstLcn returns a constant so a stable sort keeps the original order.
	internal static class LcnUtils {
		const uint FSCTL_GET_RETRIEVAL_POINTERS = 0x00090073;
		const int ERROR_MORE_DATA = 234;

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern bool DeviceIoControl(SafeFileHandle handle, uint code, ref long inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr overlapped);

		/// <summary>First-extent LCN of the file, or long.MaxValue when unavailable (non-Windows,
		/// unreadable, resident, or missing) — failures sort to the end, after real positions.</summary>
		internal static long GetFirstLcn(string path) {
			if (!OperatingSystem.IsWindows()) return long.MaxValue;
			using SafeFileHandle h = CreateFileW(path, 0x80 /*FILE_READ_ATTRIBUTES*/, 7 /*share rwd*/, IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0, IntPtr.Zero);
			if (h.IsInvalid) return long.MaxValue;
			long startVcn = 0;
			// RETRIEVAL_POINTERS_BUFFER: ExtentCount(4)+pad(4)+StartingVcn(8), extents follow as
			// [NextVcn(8), Lcn(8)]. Room for one extent — ERROR_MORE_DATA just means "file has more",
			// and the first extent is all the ordering needs (files here are rarely fragmented).
			byte[] buf = new byte[32];
			if (!DeviceIoControl(h, FSCTL_GET_RETRIEVAL_POINTERS, ref startVcn, sizeof(long), buf, buf.Length, out _, IntPtr.Zero) &&
				Marshal.GetLastWin32Error() != ERROR_MORE_DATA)
				return long.MaxValue;
			if (BitConverter.ToInt32(buf, 0) < 1) return long.MaxValue;   // resident / no extents
			long lcn = BitConverter.ToInt64(buf, 24);
			return lcn < 0 ? long.MaxValue : lcn;   // -1 = compressed/sparse hole
		}
	}
}
