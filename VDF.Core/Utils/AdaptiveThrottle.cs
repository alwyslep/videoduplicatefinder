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

namespace VDF.Core.Utils {
	/// <summary>
	/// A concurrency gate whose limit can be resized live, including while every slot stays continuously
	/// busy. Growing is trivial (release a permit, or cancel a not-yet-consumed shrink request first).
	/// Shrinking cannot forcibly evict a caller already holding a permit — instead it posts a pending
	/// retirement that the first caller(s) to finish their current unit of work consume cooperatively via
	/// <see cref="Complete"/>. This is what makes a shrink actually land under continuous full load: a
	/// naive "steal a currently-idle permit" approach never finds one free when every worker immediately
	/// re-acquires the permit it just released for its next unit of work.
	/// </summary>
	public sealed class AdaptiveThrottle : IDisposable {
		readonly SemaphoreSlim sem;
		int pendingRetire;

		public AdaptiveThrottle(int initialCount, int maxCount) => sem = new SemaphoreSlim(initialCount, maxCount);

		public Task WaitAsync(CancellationToken token = default) => sem.WaitAsync(token);

		/// <summary>Call once when a caller finishes its unit of work. Returns true if the caller should
		/// retire (stop asking for more work) instead of looping for another unit — false releases the
		/// permit back for someone else (possibly the same caller) to pick up.</summary>
		public bool Complete() {
			while (true) {
				int cur = Volatile.Read(ref pendingRetire);
				if (cur <= 0) { sem.Release(); return false; }
				if (Interlocked.CompareExchange(ref pendingRetire, cur - 1, cur) == cur) return true;
			}
		}

		/// <summary>Raises the limit by <paramref name="amount"/>, cancelling any not-yet-consumed
		/// shrink request first so a quick shrink-then-grow flip doesn't retire a worker unnecessarily.</summary>
		public void Grow(int amount) {
			for (int k = 0; k < amount; k++) {
				bool cancelled = false;
				while (true) {
					int cur = Volatile.Read(ref pendingRetire);
					if (cur <= 0) break;
					if (Interlocked.CompareExchange(ref pendingRetire, cur - 1, cur) == cur) { cancelled = true; break; }
				}
				if (!cancelled) sem.Release();
			}
		}

		/// <summary>Lowers the limit by <paramref name="amount"/>. Takes effect progressively as busy
		/// callers finish their current unit of work and call <see cref="Complete"/> — never instantly.</summary>
		public void Shrink(int amount) {
			if (amount > 0) Interlocked.Add(ref pendingRetire, amount);
		}

		public void Dispose() => sem.Dispose();
	}
}
