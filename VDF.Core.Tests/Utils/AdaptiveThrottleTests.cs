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
//     along with VideoDuplicateFinder.  If not, see <https://www.gnu.org/licenses/>.
// */
//

using VDF.Core.Utils;

namespace VDF.Core.Tests;

public class AdaptiveThrottleTests {
	// Regresses the bug where a per-drive worker cap set to a lower value never actually took effect
	// while every worker stayed continuously busy: shrinking by racing SemaphoreSlim.Wait(0) against
	// busy workers never wins, because each worker re-acquires its own just-released permit for its
	// next unit of work before an external poller can steal it. Workers must spin continuously (no
	// delay) to reproduce that race window realistically.
	// Each simulated "file" takes a measurable 20ms so a worker's active window is comfortably longer
	// than the 5ms poll interval below — Task.Yield()/near-instant work resolves faster than polling
	// can reliably sample, which produced false negatives (looked unconcurrent regardless of the real
	// permit count) unrelated to AdaptiveThrottle's own correctness.
	static Task SimulatedFile() => Task.Delay(20);

	[Fact]
	public async Task Shrink_UnderContinuousFullLoad_Converges() {
		var throttle = new AdaptiveThrottle(4, 8);
		int active = 0, maxBeforeShrink = 0, maxAfterShrink = 0;
		using var cts = new CancellationTokenSource();
		var workers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () => {
			while (!cts.IsCancellationRequested) {
				await throttle.WaitAsync(cts.Token);
				Interlocked.Increment(ref active);
				try { await SimulatedFile(); }   // continuous full load: loops straight back for more, no idle gap
				finally { Interlocked.Decrement(ref active); }
				if (throttle.Complete()) return;   // retire
			}
		})).ToArray();

		// Confirm the pool actually reaches 4 concurrent BEFORE shrinking — otherwise a later "dropped to
		// 1" observation would be meaningless (it might never have been above 1 in the first place).
		var sw = System.Diagnostics.Stopwatch.StartNew();
		while (sw.ElapsedMilliseconds < 2000) { maxBeforeShrink = Math.Max(maxBeforeShrink, Volatile.Read(ref active)); if (maxBeforeShrink >= 4) break; await Task.Delay(5); }
		Assert.True(maxBeforeShrink >= 4, $"pool never reached 4 concurrent before shrinking (max seen {maxBeforeShrink})");

		throttle.Shrink(3);   // 4 -> 1

		sw.Restart();
		bool converged = false;
		while (sw.ElapsedMilliseconds < 3000) {
			int cur = Volatile.Read(ref active);
			maxAfterShrink = Math.Max(maxAfterShrink, cur);
			if (cur <= 1) { converged = true; break; }
			await Task.Delay(5);
		}

		cts.Cancel();
		try { await Task.WhenAll(workers); } catch (OperationCanceledException) { }
		throttle.Dispose();

		Assert.True(converged, $"shrink to 1 never converged under continuous load (observed up to {maxAfterShrink} concurrent)");
	}

	[Fact]
	public async Task Grow_AfterShrink_RestoresConcurrency() {
		var throttle = new AdaptiveThrottle(1, 8);
		int active = 0;
		using var cts = new CancellationTokenSource();
		var workers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () => {
			while (!cts.IsCancellationRequested) {
				await throttle.WaitAsync(cts.Token);
				Interlocked.Increment(ref active);
				try { await SimulatedFile(); }
				finally { Interlocked.Decrement(ref active); }
				if (throttle.Complete()) return;
			}
		})).ToArray();

		throttle.Grow(3);   // 1 -> 4

		var sw = System.Diagnostics.Stopwatch.StartNew();
		int maxSeen = 0;
		while (sw.ElapsedMilliseconds < 2000) {
			maxSeen = Math.Max(maxSeen, Volatile.Read(ref active));
			if (maxSeen >= 4) break;
			await Task.Delay(5);
		}

		cts.Cancel();
		try { await Task.WhenAll(workers); } catch (OperationCanceledException) { }
		throttle.Dispose();

		Assert.True(maxSeen >= 4, $"grow to 4 never observed (max seen {maxSeen})");
	}
}
