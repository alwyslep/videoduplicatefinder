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

// The auto-delete trigger: a video is "unrecoverable" only after its frame sampling has failed the
// whole retry budget. Guards against deleting images, still-retrying files, or anything when the cap
// is disabled — the safety net behind a destructive (recycle-bin) action.
public class UnrecoverableVideoTests {
	const int Cap = 2;

	static FileEntry Make(bool image, bool thumbErr, byte failCount) {
		var e = new FileEntry();
		if (image) e.Flags.Set(EntryFlags.IsImage);
		if (thumbErr) e.Flags.Set(EntryFlags.ThumbnailError);
		e.SamplingFailCount = failCount;
		return e;
	}

	[Fact]
	public void FailedVideo_AtBudget_IsUnrecoverable() {
		Assert.True(ScanEngine.IsUnrecoverableVideo(Make(image: false, thumbErr: true, failCount: 2), Cap));
	}

	[Fact]
	public void FailedVideo_OverBudget_IsUnrecoverable() {
		Assert.True(ScanEngine.IsUnrecoverableVideo(Make(false, true, 7), Cap));
	}

	[Fact]
	public void FailedVideo_UnderBudget_IsKept() {
		// Still inside the retry budget -> not yet confirmed corrupt, must not be deleted.
		Assert.False(ScanEngine.IsUnrecoverableVideo(Make(false, true, 1), Cap));
	}

	[Fact]
	public void NoThumbnailError_IsKept() {
		Assert.False(ScanEngine.IsUnrecoverableVideo(Make(false, thumbErr: false, failCount: 5), Cap));
	}

	[Fact]
	public void Image_IsNeverDeleted() {
		Assert.False(ScanEngine.IsUnrecoverableVideo(Make(image: true, thumbErr: true, failCount: 9), Cap));
	}

	[Fact]
	public void CapDisabled_NeverDeletes() {
		// maxSamplingAttempts <= 0 means "retry forever" -> auto-delete must never fire.
		Assert.False(ScanEngine.IsUnrecoverableVideo(Make(false, true, 99), 0));
	}
}
