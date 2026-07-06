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

// The retry-budget gate: a sampling-failed file is permanently skipped when retry is off, or when
// it has burned through MaxSamplingRetryAttempts (default 2). Transient failures get their chances.
public class SamplingRetryTests {
	const int Cap = 2;

	[Fact]
	public void NotAFailure_NeverPermanent() {
		Assert.False(ScanEngine.SamplingPermanentlyFailed(hasThumbnailError: false, retryEnabled: true, failCount: 99, maxAttempts: Cap));
	}

	[Fact]
	public void RetryOff_FailedFile_IsPermanent() {
		// Old behaviour: retry disabled -> a failed file is skipped regardless of count.
		Assert.True(ScanEngine.SamplingPermanentlyFailed(true, retryEnabled: false, failCount: 0, maxAttempts: Cap));
	}

	[Fact]
	public void RetryOn_UnderBudget_KeepsRetrying() {
		Assert.False(ScanEngine.SamplingPermanentlyFailed(true, retryEnabled: true, failCount: 1, maxAttempts: Cap));
	}

	[Fact]
	public void RetryOn_AtBudget_IsPermanent() {
		Assert.True(ScanEngine.SamplingPermanentlyFailed(true, retryEnabled: true, failCount: 2, maxAttempts: Cap));
	}

	[Fact]
	public void RetryOn_OverBudget_IsPermanent() {
		Assert.True(ScanEngine.SamplingPermanentlyFailed(true, retryEnabled: true, failCount: 5, maxAttempts: Cap));
	}

	[Fact]
	public void CapZero_DisablesCap_RetriesForever() {
		// maxAttempts <= 0 -> no cap: only retry-off can make it permanent.
		Assert.False(ScanEngine.SamplingPermanentlyFailed(true, retryEnabled: true, failCount: 999, maxAttempts: 0));
		Assert.True(ScanEngine.SamplingPermanentlyFailed(true, retryEnabled: false, failCount: 0, maxAttempts: 0));
	}
}
