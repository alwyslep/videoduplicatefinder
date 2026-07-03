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

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using System.Reactive;
using ReactiveUI;
using VDF.Core.Utils;
using VDF.GUI.Data;

namespace VDF.GUI.ViewModels {
	public partial class MainWindowVM {
		ObservableCollection<DirectoryTreeNodeVM>? _directoryTreeRoots;

		// Drive-rooted, lazily-expanded folder tree behind the "Directory selection" settings tab.
		// Ticking a node adds its path to SettingsFile.Includes, unticking removes it (and its whole
		// subtree) — the same collection the classic list uses, so the two stay in sync both ways.
		// Built on first access so it costs nothing at startup.
		public ObservableCollection<DirectoryTreeNodeVM> DirectoryTreeRoots {
			get {
				if (_directoryTreeRoots != null)
					return _directoryTreeRoots;

				// Kick off the DB path index once (used for the "unscanned files" count per folder).
				DirectoryTreeNodeVM.DbIndexTask ??= Task.Run(DirectoryTreeNodeVM.LoadDbIndex);

				_directoryTreeRoots = new ObservableCollection<DirectoryTreeNodeVM>();
				var includes = SettingsFile.Instance.Includes;
				foreach (var d in DriveInfo.GetDrives()) {
					bool ready;
					try { ready = d.IsReady; } catch { ready = false; }
					if (!ready) continue;
					_directoryTreeRoots.Add(new DirectoryTreeNodeVM(d.Name, DriveDisplayName(d), includes, isDrive: true, drive: d));
				}
				// Reflect selection changes made through the classic Add/Remove list back onto the tree.
				includes.CollectionChanged += (_, _) => {
					foreach (var n in _directoryTreeRoots!)
						n.RefreshState();
				};
				// Excluded folders are hidden from the tree, so re-evaluate visibility whenever the
				// exclude list changes (drag-to-exclude, add/remove/clear in the exclude panel).
				// A new exclude also prunes include entries it swallows (equal or underneath):
				// their nodes just went invisible, so they would be unmanageable phantoms — and the
				// scan would still walk the whole excluded subtree only to reject every file.
				// Ancestor-wide includes stay (that's the carve-out case).
				SettingsFile.Instance.Blacklists.CollectionChanged += (_, e) => {
					if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add && e.NewItems != null) {
						foreach (string b in e.NewItems.OfType<string>()) {
							string entry = VDF.Core.ScanEngine.NormalizePathEntry(b);
							for (int i = includes.Count - 1; i >= 0; i--)
								if (VDF.Core.ScanEngine.IsBlackListed(includes[i], entry))
									includes.RemoveAt(i);
						}
					}
					foreach (var n in _directoryTreeRoots!)
						n.RefreshState();
					// Unscanned counts exclude blacklisted subtrees, so ancestors must recount.
					foreach (var n in _directoryTreeRoots!)
						n.RefreshStatsRecursive();
				};
				return _directoryTreeRoots;
			}
		}

		// Reload the DB index and recompute every materialised node — invoked by the toolbar refresh
		// button and automatically when a scan finishes (so the tree's counts stay current).
		void RefreshDirectoryTree() {
			if (_directoryTreeRoots == null) return;
			DirectoryTreeNodeVM.RefreshDatabaseIndex();
			foreach (var n in _directoryTreeRoots)
				n.RefreshStatsRecursive();
		}

		public ReactiveCommand<Unit, Unit> RefreshDirectoryTreeCommand => ReactiveCommand.Create(RefreshDirectoryTree);

		static string DriveDisplayName(DriveInfo d) {
			try {
				string label = d.VolumeLabel;
				return string.IsNullOrWhiteSpace(label) ? d.Name : $"{d.Name} ({label})";
			}
			catch { return d.Name; }
		}
	}

	// One folder (or drive) node. Children are enumerated lazily on first expand; folder size and the
	// "unscanned files" count are computed on a throttled background walk so the UI never blocks.
	public sealed class DirectoryTreeNodeVM : ReactiveObject {
		static readonly StringComparison PathCmp =
			OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

		// Colours (app runs the Dark theme). State drives the icon + name colour so the tree reads at a glance.
		static readonly IBrush IncludedBrush = new SolidColorBrush(Color.Parse("#66BB6A")); // green  = included
		static readonly IBrush PartialBrush  = new SolidColorBrush(Color.Parse("#FFB74D")); // amber  = partially included
		static readonly IBrush FolderBrush   = new SolidColorBrush(Color.Parse("#E6C15A")); // gold   = plain folder
		static readonly IBrush DriveBrush    = new SolidColorBrush(Color.Parse("#64B5F6")); // blue   = drive
		static readonly IBrush DefaultBrush  = new SolidColorBrush(Color.Parse("#DCDCDC")); // light  = plain name

		// Throttle the recursive size/unscanned walks so expanding a drive doesn't hammer the disk.
		static readonly SemaphoreSlim WalkGate = new(2);
		// Directory listing for the tree: skip Hidden|System (so $RECYCLE.BIN / System Volume
		// Information / hidden OS folders never appear — VDF only ever scans media folders) and
		// ReparsePoint (junction loops). Mirrors WalkFolder's file policy so tree + stats stay consistent.
		static readonly EnumerationOptions DirEnumOptions = new() {
			IgnoreInaccessible = true,
			AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
		};
		// Set of file paths already in the VDF database, for the per-folder "unscanned" count. null until loaded.
		static volatile HashSet<string>? _dbPaths;
		internal static Task? DbIndexTask;

		readonly ObservableCollection<string> _includes;
		readonly bool _isPlaceholder;
		bool _loaded;

		public string Path { get; }
		public string Name { get; }
		public bool IsDrive { get; }
		public ObservableCollection<DirectoryTreeNodeVM> Children { get; } = new();

		// Placeholder child: gives a not-yet-loaded node its expander arrow without enumerating; swapped
		// for the real children the instant the node expands, so it never actually renders.
		DirectoryTreeNodeVM() {
			_includes = new ObservableCollection<string>();
			_isPlaceholder = true;
			Path = string.Empty;
			Name = string.Empty;
		}

		public DirectoryTreeNodeVM(string path, string name, ObservableCollection<string> includes, bool isDrive, DriveInfo? drive) {
			Path = path;
			Name = name;
			IsDrive = isDrive;
			_includes = includes;

			if (isDrive && drive != null) {
				try {
					long total = drive.TotalSize;
					_sizeText = $"{FormatSize(total - drive.TotalFreeSpace)} / {FormatSize(total)}";
				}
				catch { _sizeText = string.Empty; }
			}
			else {
				_sizeText = "…";
				ComputeFolderStats();   // background, throttled
			}

			if (HasSubDirectories(path))
				Children.Add(new DirectoryTreeNodeVM());
		}

		bool _isExpanded;
		public bool IsExpanded {
			get => _isExpanded;
			set {
				this.RaiseAndSetIfChanged(ref _isExpanded, value);
				if (value) {
					if (!_loaded)
						LoadChildren();
					else
						foreach (var c in Children)
							c.RefreshStats();   // re-expand → recompute what we're looking at
				}
			}
		}

		// Tri-state selection. Displayed as a normal check / indeterminate fill / empty box.
		//   true  = this folder (or an ancestor) is in the include list  → whole subtree scanned
		//   null  = some descendant folder is included, but not this one → indeterminate
		//   false = nothing here is included
		// A click always toggles the WHOLE subtree (select-all ↔ clear-all), matching how file managers
		// behave — never a confusing three-way cycle.
		public bool? CheckState {
			get {
				if (SelfOrAncestorIncluded()) return true;
				if (DescendantIncluded()) return null;
				return false;
			}
			set {
				if (_isPlaceholder) return;
				if (SelfOrAncestorIncluded())
					ClearSubtree();     // was fully/partly selected → clear this folder and everything under it
				else
					SelectThis();       // was empty/partial       → select this whole folder
				// Includes.CollectionChanged → RefreshState() repaints every node's checkbox and colours.
			}
		}

		public IBrush IconBrush =>
			IsDrive ? DriveBrush :
			CheckState == true ? IncludedBrush :
			CheckState == null ? PartialBrush : FolderBrush;

		public IBrush NameBrush =>
			CheckState == true ? IncludedBrush :
			CheckState == null ? PartialBrush : DefaultBrush;

		public FontWeight NameWeight => CheckState == true ? FontWeight.SemiBold : FontWeight.Normal;

		// Blacklisted folders are hidden from the tree entirely — same matching rules as the
		// scanner (entry normalized, then path prefix or wildcard via ScanEngine.IsBlackListed),
		// so what the tree hides is exactly what a scan skips. Removing the exclude entry brings
		// the node straight back.
		public bool IsVisibleInTree =>
			_isPlaceholder ||
			!SettingsFile.Instance.Blacklists.Any(b =>
				VDF.Core.ScanEngine.IsBlackListed(Path, VDF.Core.ScanEngine.NormalizePathEntry(b)));

		string _sizeText = string.Empty;
		public string SizeText { get => _sizeText; private set => this.RaiseAndSetIfChanged(ref _sizeText, value); }

		string _missingText = string.Empty;
		public string MissingText {
			get => _missingText;
			private set {
				this.RaiseAndSetIfChanged(ref _missingText, value);
				this.RaisePropertyChanged(nameof(HasMissing));
			}
		}
		public bool HasMissing => _missingText.Length > 0;

		// Repaint this node (and any already-loaded descendants) from the current include list.
		internal void RefreshState() {
			if (_isPlaceholder) return;
			this.RaisePropertyChanged(nameof(CheckState));
			this.RaisePropertyChanged(nameof(IconBrush));
			this.RaisePropertyChanged(nameof(NameBrush));
			this.RaisePropertyChanged(nameof(NameWeight));
			this.RaisePropertyChanged(nameof(IsVisibleInTree));
			if (_loaded)
				foreach (var c in Children)
					c.RefreshState();
		}

		// ── selection helpers ──────────────────────────────────────────────────────────────────────
		bool SelfOrAncestorIncluded() {
			foreach (var i in _includes)
				if (Eq(i, Path) || IsUnder(Path, i)) return true;
			return false;
		}
		bool DescendantIncluded() {
			foreach (var i in _includes)
				if (IsUnder(i, Path)) return true;
			return false;
		}
		void SelectThis() {
			// Drop now-redundant descendant entries, then add this folder (unless an ancestor already covers it).
			for (int i = _includes.Count - 1; i >= 0; i--)
				if (IsUnder(_includes[i], Path)) _includes.RemoveAt(i);
			if (!SelfOrAncestorIncluded()) _includes.Add(Path);
		}
		void ClearSubtree() {
			// Remove this folder and every included descendant. (Ancestor-wide includes are left alone —
			// carving a single child out of a whole-drive include would need the exclude list.)
			for (int i = _includes.Count - 1; i >= 0; i--) {
				var p = _includes[i];
				if (Eq(p, Path) || IsUnder(p, Path)) _includes.RemoveAt(i);
			}
		}

		// ── lazy children ──────────────────────────────────────────────────────────────────────────
		void LoadChildren() {
			if (_loaded) return;
			_loaded = true;
			Children.Clear();   // drop the placeholder
			try {
				foreach (var dir in Directory.EnumerateDirectories(Path, "*", DirEnumOptions).OrderBy(d => d, StringComparer.OrdinalIgnoreCase)) {
					string leaf = System.IO.Path.GetFileName(dir);
					if (string.IsNullOrEmpty(leaf)) leaf = dir;
					Children.Add(new DirectoryTreeNodeVM(dir, leaf, _includes, isDrive: false, drive: null));
				}
			}
			catch { /* access denied / removed drive: leave it childless */ }
		}

		// ── size + unscanned count (background) ─────────────────────────────────────────────────────
		async void ComputeFolderStats() {
			// Snapshot scan-relevant settings on the UI thread — WalkFolder runs on a worker and
			// must not enumerate the live Blacklists ObservableCollection mid-mutation.
			(bool includeImages, bool recurse, string[] blacklist) cfg = (
				SettingsFile.Instance.IncludeImages,
				SettingsFile.Instance.IncludeSubDirectories,
				SettingsFile.Instance.Blacklists.Select(VDF.Core.ScanEngine.NormalizePathEntry).ToArray());
			(long size, int missing) result = (0, 0);
			try {
				if (DbIndexTask != null)
					await DbIndexTask.ConfigureAwait(false);
				await WalkGate.WaitAsync().ConfigureAwait(false);
				try {
					result = await Task.Run(() => WalkFolder(Path, cfg)).ConfigureAwait(false);
				}
				finally {
					WalkGate.Release();
				}
			}
			catch { return; }
			Dispatcher.UIThread.Post(() => {
				SizeText = FormatSize(result.size);
				MissingText = result.missing > 0
					? string.Format(App.Lang["MainWindow.Settings.DirTree.Unscanned"], result.missing)
					: string.Empty;
			});
		}

		// Recompute this node's size + unscanned count (drives read instantly; folders re-walk).
		public void RefreshStats() {
			if (_isPlaceholder) return;
			if (IsDrive) {
				try {
					var d = new DriveInfo(Path);
					long total = d.TotalSize;
					SizeText = $"{FormatSize(total - d.TotalFreeSpace)} / {FormatSize(total)}";
				}
				catch { }
				return;
			}
			SizeText = "…";
			MissingText = string.Empty;
			ComputeFolderStats();
		}

		// Refresh this node and every already-loaded descendant.
		public void RefreshStatsRecursive() {
			if (_isPlaceholder) return;
			RefreshStats();
			if (_loaded)
				foreach (var c in Children)
					c.RefreshStatsRecursive();
		}

		// Size is the physical folder size (every file, always recursive). The "unscanned" count is
		// what stage 1 (file-list building) would actually register if this folder were scanned with
		// the CURRENT settings: video-only unless 'Include images' is on, top level only unless
		// 'Include subdirectories' is on, and blacklisted subtrees pruned — so the badge matches the
		// scan instead of over-counting.
		static (long size, int missing) WalkFolder(string path, (bool includeImages, bool recurse, string[] blacklist) cfg) {
			long size = 0;
			int missing = 0;
			try {
				var opts = new EnumerationOptions {
					IgnoreInaccessible = true,
					// Skip Hidden|System (EnumerationOptions' own default, which is lost once AttributesToSkip
					// is set) so $RECYCLE.BIN / System Volume Information don't count deleted files as
					// "unscanned" or inflate the size; plus ReparsePoint to avoid junction loops.
					AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
				};
				var db = _dbPaths;
				// countable = stage 1 would visit this directory (recursion on and not excluded).
				var queue = new Queue<(DirectoryInfo dir, bool countable)>();
				queue.Enqueue((new DirectoryInfo(path), true));
				while (queue.Count > 0) {
					var (dir, countable) = queue.Dequeue();
					try {
						foreach (var fi in dir.EnumerateFiles("*", opts)) {
							size += fi.Length;
							if (!countable || db == null || db.Contains(fi.FullName))
								continue;
							string ext = System.IO.Path.GetExtension(fi.Name);
							if (cfg.includeImages ? FileUtils.IsMediaExtension(ext) : FileUtils.IsVideoExtension(ext))
								missing++;
						}
						foreach (var sub in dir.EnumerateDirectories("*", opts)) {
							bool subCountable = countable && cfg.recurse &&
								!cfg.blacklist.Any(b => VDF.Core.ScanEngine.IsBlackListed(sub.FullName, b));
							queue.Enqueue((sub, subCountable));
						}
					}
					catch { /* access denied mid-walk: skip this directory */ }
				}
			}
			catch { /* best-effort */ }
			return (size, missing);
		}

		// Reload the DB path index after the database changes (e.g. a scan finished) so the next
		// stat recompute reflects the new "unscanned" set. Re-snapshots the current in-memory DB.
		internal static void RefreshDatabaseIndex() => DbIndexTask = Task.Run(LoadDbIndex);

		internal static void LoadDbIndex() {
			var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			try {
				if (DatabaseUtils.Database.Count == 0) {
					DatabaseUtils.CustomDatabaseFolder = SettingsFile.Instance.CustomDatabaseFolder;
					DatabaseUtils.InvalidateDatabaseFolder();
					DatabaseUtils.LoadDatabase();
				}
				foreach (var e in DatabaseUtils.Database)
					set.Add(e.Path);
			}
			catch { /* no DB yet: counts simply won't show */ }
			_dbPaths = set;
		}

		// ── small utils ────────────────────────────────────────────────────────────────────────────
		static bool Eq(string a, string b) => string.Equals(a, b, PathCmp);

		// True if 'candidate' is a strict descendant path of 'ancestor' ("D:\Videos" is under "D:\",
		// but "D:\VideosX" is NOT under "D:\Videos").
		static bool IsUnder(string candidate, string ancestor) {
			if (candidate.Length <= ancestor.Length || !candidate.StartsWith(ancestor, PathCmp))
				return false;
			if (ancestor.EndsWith(System.IO.Path.DirectorySeparatorChar) || ancestor.EndsWith(System.IO.Path.AltDirectorySeparatorChar))
				return true;   // drive roots ("D:\") already end in a separator
			char boundary = candidate[ancestor.Length];
			return boundary == System.IO.Path.DirectorySeparatorChar || boundary == System.IO.Path.AltDirectorySeparatorChar;
		}

		static bool HasSubDirectories(string path) {
			try { return Directory.EnumerateDirectories(path, "*", DirEnumOptions).Any(); }
			catch { return false; }
		}

		static string FormatSize(long bytes) {
			string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
			double s = bytes;
			int i = 0;
			while (s >= 1024 && i < units.Length - 1) { s /= 1024; i++; }
			return i == 0 ? $"{bytes} {units[0]}" : $"{s:0.#} {units[i]}";
		}
	}
}
