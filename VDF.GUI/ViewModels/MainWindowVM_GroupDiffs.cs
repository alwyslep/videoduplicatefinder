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

using System.Linq;
using ReactiveUI;

namespace VDF.GUI.ViewModels {
	public partial class MainWindowVM : ReactiveObject {

		// Always-on group diff: within each group only the (first) best value keeps its real
		// value; ties show "=" and worse items show just the difference. The exact value of a
		// folded/diffed cell is available via its tooltip. Runs from RefreshGroupStats(), which
		// every group-membership change already routes through (scan done, import, deletions,
		// row removals, tombstone collapse).
		internal static void ComputeGroupDiffs(List<DuplicateItemVM> items) {
			if (items.Count == 0) return;

			// Reference per metric = first item Core flagged best; after deletions the flags can
			// all be false (they are set once at scan time), so fall back to the extreme value and
			// re-flag it — otherwise the surviving anchor would render red ("worse") with no green
			// value left in the group.
			// Size: rows without a real size (tombstone/offline, SizeLong == -1) can't anchor —
			// Core flags the -1 row IsBestSize (it is the group min), which would kill live-vs-live
			// diffs. They show their raw value and stay out of the comparison.
			var sized = items.Where(i => i.ItemInfo.SizeLong > 0).ToList();
			var refSize = sized.FirstOrDefault(i => i.ItemInfo.IsBestSize) ?? sized.MinBy(i => i.ItemInfo.SizeLong);
			if (refSize != null) refSize.ItemInfo.IsBestSize = true;
			foreach (var it in items)
				it.SizeDiff = refSize == null || ReferenceEquals(it, refSize) || it.ItemInfo.SizeLong <= 0 ? null
					: it.ItemInfo.SizeLong == refSize.ItemInfo.SizeLong ? "="
					: FormatPercentDiff((double)(it.ItemInfo.SizeLong - refSize.ItemInfo.SizeLong) / refSize.ItemInfo.SizeLong * 100);

			// Resolution: same -> "=", different -> its own shorthand (a numeric delta of WxH means
			// nothing). When the shorthand collides with the anchor's (1440x1080 vs 1920x1080 are
			// both "1080p"), show the raw WxH so the difference stays visible.
			var refFrame = items.FirstOrDefault(i => i.ItemInfo.IsBestFrameSize) ?? items.MaxBy(i => i.ItemInfo.FrameSizeInt)!;
			refFrame.ItemInfo.IsBestFrameSize = true;
			foreach (var it in items)
				it.FrameSizeDiff = ReferenceEquals(it, refFrame) ? null
					: string.Equals(it.ItemInfo.FrameSize, refFrame.ItemInfo.FrameSize, StringComparison.Ordinal) ? "="
					: it.FrameSizeDisplay == refFrame.FrameSizeDisplay ? (it.ItemInfo.FrameSize ?? string.Empty)
					: it.FrameSizeDisplay;

			// Spec string: no better/worse ranking — fold identical specs into "=" against the first row
			var refFmt = items[0];
			foreach (var it in items)
				it.FormatInfoDiff = ReferenceEquals(it, refFmt) ? null
					: string.Equals(it.FormatInfo, refFmt.FormatInfo, StringComparison.Ordinal) ? "="
					: null;   // different spec -> show its own full string

			if (items[0].ItemInfo.IsImage) {
				foreach (var it in items) {
					it.DurationDiff = null;
					it.FpsDiff = null;
					it.BitRateDiff = null;
					it.AudioBitRateDiff = null;
				}
				return;
			}

			var refDur = items.FirstOrDefault(i => i.ItemInfo.IsBestDuration) ?? items.MaxBy(i => i.ItemInfo.Duration)!;
			refDur.ItemInfo.IsBestDuration = true;
			foreach (var it in items)
				it.DurationDiff = ReferenceEquals(it, refDur) ? null
					: it.ItemInfo.Duration == refDur.ItemInfo.Duration ? "="
					: FormatDurationDiff(it.ItemInfo.Duration - refDur.ItemInfo.Duration);

			var refFps = items.FirstOrDefault(i => i.ItemInfo.IsBestFps) ?? items.MaxBy(i => i.ItemInfo.Fps)!;
			refFps.ItemInfo.IsBestFps = true;
			foreach (var it in items)
				it.FpsDiff = ReferenceEquals(it, refFps) ? null
					: it.ItemInfo.Fps == refFps.ItemInfo.Fps ? "="
					: refFps.ItemInfo.Fps > 0.01f
						? FormatPercentDiff((it.ItemInfo.Fps - refFps.ItemInfo.Fps) / refFps.ItemInfo.Fps * 100)
						: null;

			var refBr = items.FirstOrDefault(i => i.ItemInfo.IsBestBitRateKbs) ?? items.MaxBy(i => i.ItemInfo.BitRateKbs)!;
			refBr.ItemInfo.IsBestBitRateKbs = true;
			foreach (var it in items)
				it.BitRateDiff = ReferenceEquals(it, refBr) ? null
					: it.ItemInfo.BitRateKbs == refBr.ItemInfo.BitRateKbs ? "="
					: refBr.ItemInfo.BitRateKbs > 0
						? FormatPercentDiff((double)(it.ItemInfo.BitRateKbs - refBr.ItemInfo.BitRateKbs) / (double)refBr.ItemInfo.BitRateKbs * 100)
						: null;

			var refAbr = items.FirstOrDefault(i => i.ItemInfo.IsBestAudioBitRateKbs) ?? items.MaxBy(i => i.ItemInfo.AudioBitRateKbs)!;
			refAbr.ItemInfo.IsBestAudioBitRateKbs = true;
			foreach (var it in items)
				it.AudioBitRateDiff = ReferenceEquals(it, refAbr) ? null
					: it.ItemInfo.AudioBitRateKbs == refAbr.ItemInfo.AudioBitRateKbs ? "="
					: refAbr.ItemInfo.AudioBitRateKbs > 0
						? FormatPercentDiff((double)(it.ItemInfo.AudioBitRateKbs - refAbr.ItemInfo.AudioBitRateKbs) / (double)refAbr.ItemInfo.AudioBitRateKbs * 100)
						: null;
		}

		static string FormatPercentDiff(double pct) {
			if (Math.Abs(pct) < 0.5)
				return "=";
			return $"{pct:+0;-0}%";
		}

		static string FormatDurationDiff(TimeSpan diff) {
			// ffprobe durations routinely differ by milliseconds between re-encodes;
			// sub-second deltas would otherwise render as a nonsense "+0s"/"-0s".
			if (Math.Abs(diff.TotalSeconds) < 1)
				return "=";
			string sign = diff < TimeSpan.Zero ? "-" : "+";
			var abs = diff.Duration();
			if (abs.TotalHours >= 1)
				return $"{sign}{(int)abs.TotalHours}h{abs.Minutes:D2}m{abs.Seconds:D2}s";
			if (abs.TotalMinutes >= 1)
				return $"{sign}{(int)abs.TotalMinutes}m{abs.Seconds:D2}s";
			return $"{sign}{abs.Seconds}s";
		}
	}
}
