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
	/// Pure helpers for the user-managed "not a match" group blacklist. The blacklist
	/// is a list of path-sets that the user has marked as not real duplicates; a group
	/// from a scan is considered blacklisted if every current path in the group is
	/// covered by some single blacklist entry.
	/// </summary>
	public static class GroupBlacklistFilter {
		/// <summary>Prefix marking an oshash content-fingerprint token stored alongside paths in a
		/// blacklist entry. Keeping it in the same string set (rather than a new field) means the mark
		/// survives a file move/rename — path changes, oshash doesn't — with no blacklist-file format
		/// change. A real path can't collide: on Windows a bare "oshash:x" has no drive/root.</summary>
		public const string OsHashPrefix = "oshash:";
		public static string OsHashToken(string oshash) => OsHashPrefix + oshash;
		public static bool IsOsHashToken(string value) => value.StartsWith(OsHashPrefix, StringComparison.Ordinal);

		/// <summary>
		/// Returns the set of GroupIds that are fully covered by some entry in
		/// <paramref name="blacklist"/> — every current member is matched by that entry, via its
		/// path OR (surviving a move) its oshash token. Subset semantics are intentional: if the user
		/// marked {A,B,C} as not a match, a later scan finding only {A,B} is still treated as not a match.
		/// </summary>
		public static HashSet<Guid> ComputeBlacklistedGroupIds(
			IEnumerable<(Guid GroupId, string Path, string? OsHash)> items,
			IReadOnlyList<HashSet<string>> blacklist) {

			if (blacklist == null || blacklist.Count == 0)
				return new HashSet<Guid>();

			var groupItems = new Dictionary<Guid, List<(string Path, string? OsHash)>>();
			foreach (var (gid, path, oshash) in items) {
				if (!groupItems.TryGetValue(gid, out var list))
					groupItems[gid] = list = new List<(string, string?)>();
				list.Add((path, oshash));
			}

			// Manual coverage check via blackListedGroup.Contains so we always defer
			// to the blacklist set's comparer (which BlacklistStore configures with
			// the platform's path comparer for case sensitivity).
			var result = new HashSet<Guid>();
			foreach (var kv in groupItems) {
				foreach (var blackListedGroup in blacklist) {
					bool covered = true;
					foreach (var (path, oshash) in kv.Value) {
						if (blackListedGroup.Contains(path))
							continue;   // matched by path (unmoved)
						if (oshash != null && blackListedGroup.Contains(OsHashToken(oshash)))
							continue;   // matched by content oshash (moved/renamed)
						covered = false;
						break;
					}
					if (covered) {
						result.Add(kv.Key);
						break;
					}
				}
			}
			return result;
		}
	}
}
