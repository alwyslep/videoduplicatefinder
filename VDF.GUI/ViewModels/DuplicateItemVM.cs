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
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ReactiveUI;
using VDF.Core.ViewModels;
using VDF.GUI.Data;
using VDF.GUI.Utils;

namespace VDF.GUI.ViewModels {

	[DebuggerDisplay("{ItemInfo.Path,nq} - {ItemInfo.GroupId}")]
	public sealed class DuplicateItemVM : ReactiveObject, IJsonOnDeserialized {
		//For JSON deserialization only
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
		public DuplicateItemVM() { }

		public DuplicateItemVM(DuplicateItem item) {
			ItemInfo = item;
			WireThumbnailUpdates();
		}

		// When a DuplicateItemVM is restored from a saved scan results backup it is created
		// through the parameterless constructor, so the ThumbnailsUpdated handler below would
		// never be attached. Without it an explicit "load thumbnails" pass fills ItemInfo.ImageList
		// but never writes the thumbnail pack, sets ThumbnailKey, or raises the UI, leaving restored
		// rows blank forever (issue #775). Re-wire it once deserialization has populated ItemInfo.
		public void OnDeserialized() => WireThumbnailUpdates();

		void WireThumbnailUpdates() {
			if (ItemInfo == null) return;
			ItemInfo.ThumbnailsUpdated += () => {
				try {

					// Key on the content oshash (falling back to path) so a moved/renamed file keeps
					// its cached strip instead of regenerating and orphaning the old pack entry —
					// same reasoning as SurvivorThumbArchive. null oshash (sub-64KB / not yet hashed)
					// falls back to path (old behaviour). Width stays in the key so thumbnails at
					// different ThumbnailMaxWidth values don't collide; without it a re-scan at a larger
					// width keeps serving the old lower-res JPEG (AppendIfMissing never overwrites) and
					// the UI upscales it -> fuzzy (issue #776).
					string contentKey = VDF.Core.ScanEngine.GetOsHash(ItemInfo.Path) ?? ItemInfo.Path;
					var key = ThumbCacheHelpers.XxHash64Hex(
						contentKey + "|w=" + SettingsFile.Instance.ThumbnailMaxWidth);

					ThumbCacheHelpers.Provider?.AppendIfMissing(key, stream => {
						var uiBmp = ImageUtils.JoinImages(ItemInfo.ImageList, stream);
						if (uiBmp != null) {
							LRUBitmapCache.GetOrCreate(key, () => uiBmp);
						}
					});
					ThumbnailKey = key;

				}
				catch { /* ignore */ }
				Dispatcher.UIThread.Post(() => this.RaisePropertyChanged(nameof(Thumbnail)), DispatcherPriority.Render);

			};
		}
		public DuplicateItem ItemInfo { get; set; }

		// A row whose file is gone. Tombstone = intentionally deleted (drive mounted): its fingerprint
		// is kept so this "already deleted" content is recognized on a re-download. Offline = drive
		// unmounted (unplugged USB / reassigned letter): shown but never auto-targeted. Computed live,
		// so a rescan or a replugged drive re-evaluates. See TOMBSTONE-DESIGN.md.
		[JsonIgnore]
		public bool IsTombstone => ItemInfo != null && VDF.Core.ScanEngine.PathIsTombstone(ItemInfo.Path);
		[JsonIgnore]
		public bool IsOffline => ItemInfo != null && VDF.Core.ScanEngine.PathIsOffline(ItemInfo.Path);

		[JsonInclude]
		public string ThumbnailKey { get; set; }

		[JsonIgnore]
		private Bitmap? _thumbnail;

		[JsonIgnore]
		public Bitmap? Thumbnail {
			get {
				if (string.IsNullOrEmpty(ThumbnailKey)) return null;
				if (ThumbCacheHelpers.Provider == null)
#if DEBUG
					throw new InvalidOperationException("No active thumbnail provider");
#else
					return null;
#endif
				try {
					return LRUBitmapCache.GetOrCreate(ThumbnailKey, () => {
						using var s = ThumbCacheHelpers.Provider.OpenKey(ThumbnailKey);
						if (s == null) return null!;
						return new Avalonia.Media.Imaging.Bitmap(s);
					});
				}
				catch {
					return null;
				}
			}
			set => this.RaiseAndSetIfChanged(ref _thumbnail, value);
		}
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.

		bool _Checked;
		public bool Checked {
			get => _Checked;
			set => this.RaiseAndSetIfChanged(ref _Checked, value);
		}

		// Group-diff display: when non-null, shown instead of the normal value ("=" for same as
		// the group's best, "±…" for the difference). Recomputed by MainWindowVM.ComputeGroupDiffs
		// whenever group membership changes.
		string? _DurationDiff;
		[JsonIgnore]
		public string? DurationDiff {
			get => _DurationDiff;
			set => this.RaiseAndSetIfChanged(ref _DurationDiff, value);
		}

		string? _FrameSizeDiff;
		[JsonIgnore]
		public string? FrameSizeDiff {
			get => _FrameSizeDiff;
			set => this.RaiseAndSetIfChanged(ref _FrameSizeDiff, value);
		}

		string? _SizeDiff;
		[JsonIgnore]
		public string? SizeDiff {
			get => _SizeDiff;
			set => this.RaiseAndSetIfChanged(ref _SizeDiff, value);
		}

		string? _FpsDiff;
		[JsonIgnore]
		public string? FpsDiff {
			get => _FpsDiff;
			set => this.RaiseAndSetIfChanged(ref _FpsDiff, value);
		}

		string? _BitRateDiff;
		[JsonIgnore]
		public string? BitRateDiff {
			get => _BitRateDiff;
			set => this.RaiseAndSetIfChanged(ref _BitRateDiff, value);
		}

		string? _AudioBitRateDiff;
		[JsonIgnore]
		public string? AudioBitRateDiff {
			get => _AudioBitRateDiff;
			set => this.RaiseAndSetIfChanged(ref _AudioBitRateDiff, value);
		}

		string? _FormatInfoDiff;
		[JsonIgnore]
		public string? FormatInfoDiff {
			get => _FormatInfoDiff;
			set => this.RaiseAndSetIfChanged(ref _FormatInfoDiff, value);
		}

		// p-class shorthand: standard ladder heights collapse to "1080p"/"4K"; anything else
		// (cinemascope 1920x800, portrait, photos) keeps the raw WxH so odd sizes stay distinguishable.
		internal static string FrameSizeToDisplay(string? frameSize) {
			if (string.IsNullOrEmpty(frameSize)) return string.Empty;
			int x = frameSize.IndexOf('x');
			if (x <= 0 || !int.TryParse(frameSize.AsSpan(x + 1), out int h))
				return frameSize;
			return h switch {
				4320 => "8K",
				2160 => "4K",
				1440 => "1440p",
				1080 => "1080p",
				720 => "720p",
				576 => "576p",
				480 => "480p",
				360 => "360p",
				240 => "240p",
				_ => frameSize
			};
		}

		[JsonIgnore]
		public string FrameSizeDisplay => FrameSizeToDisplay(ItemInfo?.FrameSize);

		// Merged spec column: video codec · audio codec · channels · sample rate · HDR.
		// These rarely differ within a group, so they fold into a single "=" most of the time.
		[JsonIgnore]
		public string FormatInfo {
			get {
				var i = ItemInfo;
				if (i == null) return string.Empty;
				var parts = new List<string>(5);
				if (!string.IsNullOrEmpty(i.Format)) parts.Add(i.Format);
				if (!string.IsNullOrEmpty(i.AudioFormat)) parts.Add(i.AudioFormat);
				if (!string.IsNullOrEmpty(i.AudioChannel)) parts.Add(i.AudioChannel == "stereo" ? "st" : i.AudioChannel);
				if (i.AudioSampleRate > 0)
					parts.Add(i.AudioSampleRate % 1000 == 0 ? $"{i.AudioSampleRate / 1000}k" : $"{i.AudioSampleRate / 1000.0:0.#}k");
				if (!string.IsNullOrEmpty(i.HdrFormat)) parts.Add(i.HdrFormat);
				return string.Join("·", parts);
			}
		}

		/// <summary>
		///   Returns if item matches the filter conditions
		/// </summary>
		[JsonIgnore]
		internal bool IsVisibleInFilter = true;

		public bool EqualsFull(DuplicateItemVM other) {
			if (ReferenceEquals(null, other)) return false;
			if (ReferenceEquals(this, other)) return true;
			return ItemInfo.SizeLong == other.ItemInfo.SizeLong &&
				   ItemInfo.GroupId.Equals(other.ItemInfo.GroupId) &&
				   ItemInfo.Duration.Equals(other.ItemInfo.Duration) &&
				   ItemInfo.FrameSizeInt == other.ItemInfo.FrameSizeInt &&
				   string.Equals(ItemInfo.Format, other.ItemInfo.Format) &&
				   string.Equals(ItemInfo.AudioFormat, other.ItemInfo.AudioFormat) &&
				   string.Equals(ItemInfo.AudioChannel, other.ItemInfo.AudioChannel) &&
				   ItemInfo.AudioSampleRate == other.ItemInfo.AudioSampleRate &&
				   ItemInfo.BitRateKbs == other.ItemInfo.BitRateKbs && ItemInfo.Fps.Equals(other.ItemInfo.Fps);
		}
		public bool EqualsButSize(DuplicateItemVM other) {
			if (ReferenceEquals(null, other)) return false;
			if (ReferenceEquals(this, other)) return true;
			return ItemInfo.GroupId.Equals(other.ItemInfo.GroupId) &&
				   ItemInfo.Duration.Equals(other.ItemInfo.Duration) &&
				   ItemInfo.FrameSizeInt == other.ItemInfo.FrameSizeInt &&
				   string.Equals(ItemInfo.Format, other.ItemInfo.Format) &&
				   string.Equals(ItemInfo.AudioFormat, other.ItemInfo.AudioFormat) &&
				   string.Equals(ItemInfo.AudioChannel, other.ItemInfo.AudioChannel) &&
				   ItemInfo.AudioSampleRate == other.ItemInfo.AudioSampleRate &&
				   ItemInfo.BitRateKbs == other.ItemInfo.BitRateKbs &&
				   ItemInfo.Fps.Equals(other.ItemInfo.Fps);
		}
		public bool EqualsButQuality(DuplicateItemVM other) {
			if (ReferenceEquals(null, other)) return false;
			if (ReferenceEquals(this, other)) return true;
			return ItemInfo.GroupId.Equals(other.ItemInfo.GroupId);
		}
		public bool EqualsOnlyLength(DuplicateItemVM other) {
			if (ReferenceEquals(null, other)) return false;
			if (ReferenceEquals(this, other)) return true;
			return ItemInfo.GroupId.Equals(other.ItemInfo.GroupId) && ItemInfo.Duration.Equals(other.ItemInfo.Duration);
		}

	}
}
