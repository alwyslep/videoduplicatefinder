// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GPLv3 as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
//     VideoDuplicateFinder is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU General Public License for more details.
//     You should have received a copy of the GNU General Public License
//     along with VideoDuplicateFinder.  If not, see <http://www.gnu.org/licenses/>.
// */
//

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.Media;
using FFmpeg.AutoGen;
using ReactiveUI;
using VDF.Core;
using VDF.Core.Utils;
using VDF.Core.ViewModels;
using VDF.GUI.Data;
using VDF.GUI.Views;

namespace VDF.GUI.ViewModels {
	public partial class MainWindowVM : ReactiveObject {
		public ScanEngine Scanner { get; } = new();

		// ── Per-drive segmented scan progress (status bar). Built once per scan; Fraction/Label update live. ──
		public ObservableCollection<DriveProgressVM> DriveSegments { get; } = new();
		bool _ShowDriveProgress;
		public bool ShowDriveProgress {
			get => _ShowDriveProgress;
			set => this.RaiseAndSetIfChanged(ref _ShowDriveProgress, value);
		}
		// Single current-file/compare text next to the bars — shown only during the
		// compare phases now that the drive bars stay visible through them.
		bool _ShowCompareText;
		public bool ShowCompareText {
			get => _ShowCompareText;
			set => this.RaiseAndSetIfChanged(ref _ShowCompareText, value);
		}
		// Drive bars are a persistent dashboard (user rule 2026-07-05): visible whenever
		// segments exist EXCEPT while the duplicate-results list holds items. Live scan
		// events force-show separately in UpdateDriveSegments; this applies at rest
		// points and on every results-collection change (including deleting the last row).
		void UpdateDriveBarVisibility() => ShowDriveProgress = DriveSegments.Count > 0 && Duplicates.Count == 0;
		static readonly IBrush[] _drivePalette = {
			new SolidColorBrush(Color.Parse("#4FC3F7")), new SolidColorBrush(Color.Parse("#81C784")),
			new SolidColorBrush(Color.Parse("#FFB74D")), new SolidColorBrush(Color.Parse("#BA68C8")),
			new SolidColorBrush(Color.Parse("#E57373")), new SolidColorBrush(Color.Parse("#4DD0E1")),
			new SolidColorBrush(Color.Parse("#FFF176")), new SolidColorBrush(Color.Parse("#A1887F")),
		};
		static IBrush DriveBrush(int i) => _drivePalette[((i % _drivePalette.Length) + _drivePalette.Length) % _drivePalette.Length];
		// Rebuild segments only when the drive set changes; otherwise just refresh each fraction/label.
		void UpdateDriveSegments(DriveProgress[]? drives) {
			// Compare phases run with driveCounters nulled (InitProgress), so their Progress events
			// carry no drives — keep the bars as they are (persistent dashboard) and surface the
			// live "comparing X/Y" text alongside them via ShowCompareText.
			if (drives == null || drives.Length == 0) { ShowCompareText = true; return; }
			ShowCompareText = false;
			bool sameSet = DriveSegments.Count == drives.Length;
			if (sameSet)
				for (int i = 0; i < drives.Length; i++)
					if (!string.Equals(DriveSegments[i].Root, drives[i].Root, System.StringComparison.OrdinalIgnoreCase)) { sameSet = false; break; }
			if (!sameSet) {
				DriveSegments.Clear();
				for (int i = 0; i < drives.Length; i++) {
					var vm = new DriveProgressVM(drives[i].Root, DriveBrush(i), drives[i].TotalBytes) { SetCap = Scanner.SetDriveCap, SetEnabled = Scanner.SetDriveEnabled };
					// Restore the persisted pause state (setter pushes it to the engine too).
					if (SettingsFile.Instance.DriveDisabledDrives.Contains(drives[i].Root))
						vm.IsActive = false;
					DriveSegments.Add(vm);
				}
			}
			// Every tick (not just on segment rebuild): BuildDriveCounters recreates the engine's
			// DriveCounter objects with CapOverride=0 at the start of EVERY scan, even one over an
			// unchanged drive set (segments stay the same VM instances, so the rebuild branch above
			// doesn't rerun) — so the saved cap must be re-pushed here to survive a second scan.
			for (int i = 0; i < drives.Length; i++) {
				if (SettingsFile.Instance.DriveParallelismCaps.TryGetValue(drives[i].Root, out var savedCap) && savedCap > 0)
					DriveSegments[i].CapIndex = DriveProgressVM.CapIndexFor(savedCap);
				// Same re-push rationale as the caps above (engine counters are recreated per
				// scan/stage with Disabled=false), but session-only: the checkbox is the truth.
				if (!DriveSegments[i].IsActive)
					Scanner.SetDriveEnabled(drives[i].Root, false);
			}
			for (int i = 0; i < drives.Length; i++) {
				var d = drives[i];
				var seg = DriveSegments[i];
				seg.Fraction = d.TotalBytes > 0 ? (double)d.DoneBytes / d.TotalBytes : 0;
				// "분석 N" = files that actually ran a tool this scan, "부재 N" = entries whose file is
				// gone from its recorded path — together they explain a bar that fills in seconds
				// (everything else completed instantly from cache/flags), so 100% no longer reads
				// as "this drive was fully re-analysed".
				seg.Label = $"{d.Root}  {seg.Fraction * 100:0}%  {d.DoneFiles:N0}/{d.TotalFiles:N0}"
					// Seconds-per-file (user request): at ~0.02-0.5 f/s the f/s figure rendered
					// as a meaningless "0.0", and the worker count is already visible as the
					// number of processing rows. Updates once per adaptive window.
					+ (d.FilesPerSec > 0 ? $"  ·  {1 / d.FilesPerSec:0.0} s/f" : "")
					// Live segment-decode threads chewing this drive's audio (process-wide gate slots).
					+ (d.DecodeWorkers > 0 ? $"  ·  {App.Lang["Drive.Decode"]} x{d.DecodeWorkers}" : "")
					+ (d.Analyzed > 0 ? $"  ·  {App.Lang["Drive.Analyzed"]} {d.Analyzed:N0}" : "")
					+ (d.Missing > 0 ? $"  ·  {App.Lang["Drive.Missing"]} {d.Missing:N0}" : "")
					// Cumulative DB inventory (grows across scans), NOT this scan's progress — the
					// done/total on the left resets every scan, which kept reading as "fingerprints".
					+ (d.FingerprintTarget > 0 ? $"  ·  {App.Lang["Drive.Fingerprints"]} {d.Fingerprinted:N0}/{d.FingerprintTarget:N0}" : "");
				// One row per worker currently active on this drive — the collection's length IS the
				// live concurrency shown to the user, so 2+ workers naturally render 2+ rows.
				// Rows are reused index-matched (update, not Clear+Add) so the right-side stage
				// gauge updates its value in place instead of re-creating controls every tick.
				int rowCount = 0;
				if (d.ActiveFiles != null)
					foreach (var af in d.ActiveFiles) {
						ActiveFileRowVM row;
						if (rowCount < seg.ActiveFileLines.Count) row = seg.ActiveFileLines[rowCount];
						else seg.ActiveFileLines.Add(row = new ActiveFileRowVM());
						rowCount++;
						// The numeric x/y moved into the gauge; stages without progress keep text only.
						row.Text = string.IsNullOrEmpty(af.Stage) ? af.File : $"{af.File}  [{af.Stage}]";
						bool hasProgress = af.StageMax > 0;
						row.HasProgress = hasProgress;
						row.Fill = seg.Fill;
						double frac = hasProgress ? Math.Clamp((double)af.StageCurrent / af.StageMax, 0, 1) : 0;
						row.Fraction = frac;
						row.PercentText = hasProgress ? $"{frac * 100:0}%" : string.Empty;
					}
				while (seg.ActiveFileLines.Count > rowCount)
					seg.ActiveFileLines.RemoveAt(seg.ActiveFileLines.Count - 1);
			}
			// Live scan always shows; the startup preview (not scanning) still respects
			// the results-list rule so imported/backup results keep their screen space.
			ShowDriveProgress = IsScanning || Duplicates.Count == 0;
		}
		public ObservableCollection<string> LogItems { get; } = new();
		List<HashSet<string>> GroupBlacklist = new();
		public string BackupScanResultsFile =>
			Path.Combine(CoreUtils.ResolveDatabaseFolder(SettingsFile.Instance.CustomDatabaseFolder), "backup.scanresults");
		public string BlacklistedGroupsFile =>
			Path.Combine(CoreUtils.ResolveDatabaseFolder(SettingsFile.Instance.CustomDatabaseFolder), "BlacklistedGroups.json");

		// Older builds wrote BlacklistedGroups.json next to the executable; current
		// code keeps it alongside the rest of the per-user database state. Move
		// the file once if a legacy copy is present and the new location is empty.
		void MigrateLegacyBlacklistLocation() {
			try {
				string legacy = Path.Combine(CoreUtils.CurrentFolder, "BlacklistedGroups.json");
				string current = BlacklistedGroupsFile;
				var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
				if (string.Equals(Path.GetFullPath(legacy), Path.GetFullPath(current), cmp)) return;
				if (File.Exists(legacy) && !File.Exists(current)) {
					Directory.CreateDirectory(Path.GetDirectoryName(current)!);
					File.Move(legacy, current);
					Logger.Instance.Info($"Migrated BlacklistedGroups.json from {legacy} to {current}");
				}
			}
			catch (Exception ex) {
				Logger.Instance.Info($"Failed to migrate BlacklistedGroups.json: {ex.Message}");
			}
		}

		public AvaloniaList<DuplicateItemVM> Duplicates { get; } = new();

		private TempDir? TempDirectory;

		static readonly string[] BusyMessages =
		{
			App.Lang["BusyMessages1"],
			App.Lang["BusyMessages2"],
			App.Lang["BusyMessages3"],
			App.Lang["BusyMessages4"],
			App.Lang["BusyMessages5"],
			App.Lang["BusyMessages6"],
			App.Lang["BusyMessages7"],
			App.Lang["BusyMessages8"],
			App.Lang["BusyMessages9"],
			App.Lang["BusyMessages10"],
			App.Lang["BusyMessages11"],
			App.Lang["BusyMessages12"],
			App.Lang["BusyMessages13"],
			App.Lang["BusyMessages14"],
			App.Lang["BusyMessages15"],
			App.Lang["BusyMessages16"],
			App.Lang["BusyMessages17"],
			App.Lang["BusyMessages18"],
			App.Lang["BusyMessages19"],
			App.Lang["BusyMessages20"]
		};
		private void ChangeIsBusyMessage() => IsBusyOverlayText = BusyMessages[Random.Shared.Next(BusyMessages.Length)];

		readonly DispatcherTimer scheduledScanTimer = new();
		DateTime lastScheduledScanDate = DateTime.MinValue;
		bool scheduledScanInProgress;
		// A search-side stage run (file list / fingerprint button) ends on BuildingHashesDone
		// instead of ScanDone; this flag tells that handler the run is his to finish.
		bool stageOnlyRunInProgress;
		bool scheduleTimeInvalidNotified;

		bool _IsScanning;
		public bool IsScanning {
			get => _IsScanning;
			set {
				this.RaiseAndSetIfChanged(ref _IsScanning, value);
				ApplyScanProcessPriority(value);
			}
		}

		// Yield to other apps during a scan when the user opted in (see LowerPriorityDuringScan). Only
		// touches priority when the toggle is on, and always restores Normal when the scan ends.
		static void ApplyScanProcessPriority(bool scanning) {
			if (!SettingsFile.Instance.LowerPriorityDuringScan)
				return;
			try {
				System.Diagnostics.Process.GetCurrentProcess().PriorityClass = scanning
					? System.Diagnostics.ProcessPriorityClass.BelowNormal
					: System.Diagnostics.ProcessPriorityClass.Normal;
			}
			catch { /* priority is best-effort; never break a scan over it */ }
		}
		string _IsBusyOverlayText = string.Empty;
		public string IsBusyOverlayText {
			get => _IsBusyOverlayText;
			set => this.RaiseAndSetIfChanged(ref _IsBusyOverlayText, value);
		}
		// Overlay heading. "Please wait" while work is actually running; replaced with an accurate
		// state ("Paused", "Stopping…") when it isn't — a paused scan showing "please wait" + a random
		// quip reads as the app still doing something, when it's waiting for the USER (Resume).
		string _BusyHeadingText = App.Lang["MainWindow.Busy.PleaseWait"];
		public string BusyHeadingText {
			get => _BusyHeadingText;
			set => this.RaiseAndSetIfChanged(ref _BusyHeadingText, value);
		}
		// Pause is two-phase: "pausing…" while in-flight files drain to 100% (overlay bar keeps
		// sweeping, in orange, like Stop's drain), then "paused" with a static bar once the last
		// worker parks — detected when a progress snapshot arrives with zero active rows.
		bool _IsPauseDraining;
		public bool IsPauseDraining {
			get => _IsPauseDraining;
			set { this.RaiseAndSetIfChanged(ref _IsPauseDraining, value); this.RaisePropertyChanged(nameof(BusyBarIsIndeterminate)); }
		}
		// True while a phase-level stage (e.g. "disk layout ordering") has replaced the overlay
		// quip — the phase's closing push restores the quip exactly once.
		bool overlayStageActive;
		bool _IsReadyToCompare;
		public bool IsReadyToCompare {
			get => _IsReadyToCompare;
			set => this.RaiseAndSetIfChanged(ref _IsReadyToCompare, value);
		}
		bool _IsGathered;
		public bool IsGathered {
			get => _IsGathered;
			set => this.RaiseAndSetIfChanged(ref _IsGathered, value);
		}
		bool _IsPaused;
		public bool IsPaused {
			get => _IsPaused;
			set { this.RaiseAndSetIfChanged(ref _IsPaused, value); this.RaisePropertyChanged(nameof(BusyBarIsIndeterminate)); }
		}
		// The "please wait" overlay bar shows a real fill % during counted stages (compare, gather —
		// ShowScanProgressBar drives ScanProgressValue/Max), and falls back to an indeterminate sweep
		// for uncounted busy states (loading/cleaning DB) and while a pause is draining. Notified from
		// the three inputs' setters below.
		public bool BusyBarIsIndeterminate => IsPauseDraining || (!IsPaused && !ShowScanProgressBar);
		bool _ShowThumbnailRetrievalProgressBar;
		public bool ShowThumbnailRetrievalProgressBar {
			get => _ShowThumbnailRetrievalProgressBar;
			set => this.RaiseAndSetIfChanged(ref _ShowThumbnailRetrievalProgressBar, value);
		}
		public bool ShowThumbnailRetrievalProgress => !string.IsNullOrEmpty(ThumbnailRetrievalProgressText);
		string _ThumbnailRetrievalProgressText = string.Empty;
		public string ThumbnailRetrievalProgressText {
			get => _ThumbnailRetrievalProgressText;
			set {
				this.RaiseAndSetIfChanged(ref _ThumbnailRetrievalProgressText, value);
				this.RaisePropertyChanged(nameof(ShowThumbnailRetrievalProgress));
			}
		}
		string _ScanProgressText = string.Empty;
		public string ScanProgressText {
			get => _ScanProgressText;
			set => this.RaiseAndSetIfChanged(ref _ScanProgressText, value);
		}
		string _RemainingTime = string.Empty;
		public string RemainingTime {
			get => _RemainingTime;
			set => this.RaiseAndSetIfChanged(ref _RemainingTime, value);
		}

		string _TimeElapsed = string.Empty;
		public string TimeElapsed {
			get => _TimeElapsed;
			set => this.RaiseAndSetIfChanged(ref _TimeElapsed, value);
		}
		int _ScanProgressValue;
		public int ScanProgressValue {
			get => _ScanProgressValue;
			set => this.RaiseAndSetIfChanged(ref _ScanProgressValue, value);
		}
		// Status-bar scan bar visibility: shown while a run reports progress, then held at 100%
		// for a beat on completion (FlashScanCompleteThenHide) before hiding, so a finished run
		// reads as "done" instead of vanishing at ~97% (the denominator counts paused-drive/out-
		// of-scope entries this run never processes, so the live counter never reaches the max).
		bool _ShowScanProgressBar;
		public bool ShowScanProgressBar {
			get => _ShowScanProgressBar;
			set { this.RaiseAndSetIfChanged(ref _ShowScanProgressBar, value); this.RaisePropertyChanged(nameof(BusyBarIsIndeterminate)); }
		}
		bool _IsBusy;
		public bool IsBusy {
			get => _IsBusy;
			set => this.RaiseAndSetIfChanged(ref _IsBusy, value);
		}
		int _ScanProgressMaxValue = 100;
		public int ScanProgressMaxValue {
			get => _ScanProgressMaxValue;
			set => this.RaiseAndSetIfChanged(ref _ScanProgressMaxValue, value);
		}
		string _ScanProgressCount = string.Empty;
		public string ScanProgressCount {
			get => _ScanProgressCount;
			set => this.RaiseAndSetIfChanged(ref _ScanProgressCount, value);
		}
		int _TotalDuplicates;
		public int TotalDuplicates {
			get => _TotalDuplicates;
			set => this.RaiseAndSetIfChanged(ref _TotalDuplicates, value);
		}
		int _TotalDuplicateGroups;
		public int TotalDuplicateGroups {
			get => _TotalDuplicateGroups;
			set => this.RaiseAndSetIfChanged(ref _TotalDuplicateGroups, value);
		}
		string _TotalDuplicatesSize = string.Empty;
		public string TotalDuplicatesSize {
			get => _TotalDuplicatesSize;
			set => this.RaiseAndSetIfChanged(ref _TotalDuplicatesSize, value);
		}
		string _PotentialSavings = string.Empty;
		/// <summary>Space freed if only the largest file of every group were kept.</summary>
		public string PotentialSavings {
			get => _PotentialSavings;
			set => this.RaiseAndSetIfChanged(ref _PotentialSavings, value);
		}
		long _TotalSizeRemovedInternal;
		long TotalSizeRemovedInternal {
			get => _TotalSizeRemovedInternal;
			set {
				_TotalSizeRemovedInternal = value;
				this.RaisePropertyChanged(nameof(TotalSizeRemoved));
			}
		}
		int _DuplicatesCheckedCounter;
		public int DuplicatesCheckedCounter {
			get => _DuplicatesCheckedCounter;
			set => this.RaiseAndSetIfChanged(ref _DuplicatesCheckedCounter, value);
		}
		long _DuplicatesCheckedSizeInternal;
		long DuplicatesCheckedSizeInternal {
			get => _DuplicatesCheckedSizeInternal;
			set {
				_DuplicatesCheckedSizeInternal = value;
				this.RaisePropertyChanged(nameof(DuplicatesCheckedSize));
			}
		}
		public string DuplicatesCheckedSize => DuplicatesCheckedSizeInternal.BytesToString();
		// SizeLong is -1 when the file no longer exists on disk; don't let those
		// items distort the checked-size total.
		static long CheckedSizeOf(DuplicateItemVM item) => Math.Max(0, item.ItemInfo.SizeLong);

		// ---- Selection undo (Ctrl+Z) ----
		// Each step holds the items whose Checked state changed in one gesture and
		// the value to restore. Bulk selection commands group their changes via
		// BeginSelectionUndoBatch(); ungrouped changes (single checkbox clicks)
		// become one step each.
		const int MaxSelectionUndoSteps = 200;
		readonly List<List<(DuplicateItemVM Item, bool OldChecked)>> selectionUndoStack = new();
		List<(DuplicateItemVM Item, bool OldChecked)>? activeSelectionUndoBatch;
		bool suppressSelectionUndoRecording;

		internal IDisposable BeginSelectionUndoBatch() {
			// Nested scopes (e.g. a preset auto-applied from another command) fold
			// into the outer step so one Ctrl+Z reverts the whole gesture.
			if (activeSelectionUndoBatch != null)
				return System.Reactive.Disposables.Disposable.Empty;
			var batch = activeSelectionUndoBatch = new();
			return System.Reactive.Disposables.Disposable.Create(() => {
				activeSelectionUndoBatch = null;
				if (batch.Count > 0)
					PushSelectionUndoStep(batch);
			});
		}

		void RecordCheckedChangeForUndo(DuplicateItemVM item) {
			if (suppressSelectionUndoRecording) return;
			var entry = (item, !item.Checked); // PropertyChanged fires after the change
			if (activeSelectionUndoBatch != null) {
				activeSelectionUndoBatch.Add(entry);
				return;
			}
			PushSelectionUndoStep(new() { entry });
		}

		void PushSelectionUndoStep(List<(DuplicateItemVM Item, bool OldChecked)> step) {
			selectionUndoStack.Add(step);
			if (selectionUndoStack.Count > MaxSelectionUndoSteps)
				selectionUndoStack.RemoveAt(0);
		}

		public ReactiveCommand<Unit, Unit> UndoSelectionCommand => ReactiveCommand.Create(() => {
			if (selectionUndoStack.Count == 0) return;
			var step = selectionUndoStack[^1];
			selectionUndoStack.RemoveAt(selectionUndoStack.Count - 1);
			suppressSelectionUndoRecording = true;
			try {
				// Restore in reverse so items touched twice in one step end up
				// at their original state.
				for (int i = step.Count - 1; i >= 0; i--)
					step[i].Item.Checked = step[i].OldChecked;
			}
			finally {
				suppressSelectionUndoRecording = false;
			}
		});

		// Live count of checked items per group, maintained alongside the checked
		// counters. The "groups with checked items" filter and sort previously
		// rescanned the whole duplicates list per row, which is quadratic on
		// large result sets.
		readonly Dictionary<Guid, int> checkedCountByGroup = new();
		internal bool GroupHasCheckedItems(Guid groupId) => checkedCountByGroup.ContainsKey(groupId);
		void AdjustCheckedGroupIndex(DuplicateItemVM item, int delta) {
			Guid groupId = item.ItemInfo.GroupId;
			checkedCountByGroup.TryGetValue(groupId, out int count);
			count += delta;
			if (count <= 0)
				checkedCountByGroup.Remove(groupId);
			else
				checkedCountByGroup[groupId] = count;
		}
		public bool IsMultiOpenSupported => !string.IsNullOrEmpty(SettingsFile.Instance.CustomCommands.OpenMultiple);
		public bool IsMultiOpenInFolderSupported => !string.IsNullOrEmpty(SettingsFile.Instance.CustomCommands.OpenMultipleInFolder);

		public bool IsWindows => CoreUtils.IsWindows;

		public string TotalSizeRemoved => TotalSizeRemovedInternal.BytesToString();
#if DEBUG
		public static bool IsDebug => true;
#else
		public static bool IsDebug => false;
#endif

		// ILLink flags every WhenAnyValue call (IL2026): it reflects over the member
		// chain in the expression. All chains here target view-model properties that
		// are also statically referenced (compiled bindings and code), so they are
		// preserved and the warning is a false positive — suppressed per call site.
		internal const string WhenAnyValueTrimJustification =
			"WhenAnyValue reflects over view-model properties that are also statically referenced (compiled bindings and code), so they are preserved.";

		[System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = WhenAnyValueTrimJustification)]
		public MainWindowVM() {
			MigrateLegacyBlacklistLocation();
			GroupBlacklist = BlacklistStore.Load(BlacklistedGroupsFile, msg => Logger.Instance.Info(msg));
			_FileType = TypeFilters[0];
			Scanner.ScanAborted += Scanner_ScanAborted;
			Scanner.ScanDone += Scanner_ScanDone;
			// Persistent drive-bar rule: any change to the results list (populate, clean,
			// user deleting the last row) re-evaluates the bars' visibility.
			Duplicates.CollectionChanged += (_, _) => UpdateDriveBarVisibility();
			Scanner.BuildingHashesDone += Scanner_BuildingHashesDone;
			Scanner.Progress += Scanner_Progress;
			Scanner.ThumbnailProgress += Scanner_ThumbnailProgress;
			Scanner.ThumbnailsRetrieved += Scanner_ThumbnailsRetrieved;
			Scanner.DatabaseCleaned += Scanner_DatabaseCleaned;
			Scanner.FilesEnumerated += Scanner_FilesEnumerated;
			using (var assetStream = AssetLoader.Open(new Uri("avares://VDF.GUI/Assets/icon.png")))
			using (var assetMs = new MemoryStream()) {
				assetStream.CopyTo(assetMs);
				Scanner.NoThumbnailImage = assetMs.ToArray();
			}

			try {
				TempDirectory = TempExtractionManager.Register(new("VDF-"));
				// Invalidate thumbnail cache if the configured width has changed
				Utils.ThumbCacheHelpers.InvalidateIfWidthChanged(TempDirectory.Path, SettingsFile.Instance.ThumbnailMaxWidth);
				Utils.ThumbCacheHelpers.Provider = Utils.ThumbPack.Open(TempDirectory.Path);
			}
			catch { Utils.ThumbCacheHelpers.Provider = null; }

			try {
				File.Delete(Path.Combine(CoreUtils.CurrentFolder, "log.txt"));
			}
			catch { }
			Logger.Instance.LogItemAdded += Instance_LogItemAdded;
			//Ensure items added before GUI was ready will be shown
			Instance_LogItemAdded(string.Empty);

			Duplicates.CollectionChanged += Duplicates_CollectionChanged;

			scheduledScanTimer.Interval = TimeSpan.FromMinutes(1);
			scheduledScanTimer.Tick += (_, __) => CheckScheduledScan();
			scheduledScanTimer.Start();
			CheckScheduledScan();

			SortOrders = new SortOrderOption[] {
				new SortOrderOption("None", null),
				new SortOrderOption("Size Ascending",
				DataGridSortDescription.FromPath($"{nameof(DuplicateItemVM.ItemInfo)}.{nameof(DuplicateItem.SizeLong)}", ListSortDirection.Ascending)),
				new SortOrderOption("Size Descending",
				DataGridSortDescription.FromPath($"{nameof(DuplicateItemVM.ItemInfo)}.{nameof(DuplicateItem.SizeLong)}", ListSortDirection.Descending)),
				new SortOrderOption("Resolution Ascending",
				DataGridSortDescription.FromPath($"{nameof(DuplicateItemVM.ItemInfo)}.{nameof(DuplicateItem.FrameSizeInt)}", ListSortDirection.Ascending)),
				new SortOrderOption("Resolution Descending",
				DataGridSortDescription.FromPath($"{nameof(DuplicateItemVM.ItemInfo)}.{nameof(DuplicateItem.FrameSizeInt)}", ListSortDirection.Descending)),
				new SortOrderOption("Duration Ascending",
				DataGridSortDescription.FromPath($"{nameof(DuplicateItemVM.ItemInfo)}.{nameof(DuplicateItem.Duration)}", ListSortDirection.Ascending)),
				new SortOrderOption("Duration Descending",
				DataGridSortDescription.FromPath($"{nameof(DuplicateItemVM.ItemInfo)}.{nameof(DuplicateItem.Duration)}", ListSortDirection.Descending)),
				new SortOrderOption("Date Created Ascending",
				DataGridSortDescription.FromPath($"{nameof(DuplicateItemVM.ItemInfo)}.{nameof(DuplicateItem.DateCreated)}", ListSortDirection.Ascending)),
				new SortOrderOption("Date Created Descending",
				DataGridSortDescription.FromPath($"{nameof(DuplicateItemVM.ItemInfo)}.{nameof(DuplicateItem.DateCreated)}", ListSortDirection.Descending)),
				new SortOrderOption("Similarity Ascending",
				DataGridSortDescription.FromPath($"{nameof(DuplicateItemVM.ItemInfo)}.{nameof(DuplicateItem.Similarity)}", ListSortDirection.Ascending)),
				new SortOrderOption("Similarity Descending",
				DataGridSortDescription.FromPath($"{nameof(DuplicateItemVM.ItemInfo)}.{nameof(DuplicateItem.Similarity)}", ListSortDirection.Descending)),
				new SortOrderOption("Group Has Selected Items Ascending",
				DataGridSortDescription.FromComparer(new CheckedGroupsComparer(this), ListSortDirection.Ascending)),
				new SortOrderOption("Group Has Selected Items Descending",
				DataGridSortDescription.FromComparer(new CheckedGroupsComparer(this), ListSortDirection.Descending)),
				new SortOrderOption("Group Size Ascending",
				DataGridSortDescription.FromComparer(new GroupSizeComparer(this), ListSortDirection.Ascending)),
				new SortOrderOption("Group Size Descending",
				DataGridSortDescription.FromComparer(new GroupSizeComparer(this), ListSortDirection.Descending)),
				new SortOrderOption("Group Total Size Ascending",
				DataGridSortDescription.FromComparer(new GroupTotalSizeComparer(this), ListSortDirection.Ascending)),
				new SortOrderOption("Group Total Size Descending",
				DataGridSortDescription.FromComparer(new GroupTotalSizeComparer(this), ListSortDirection.Descending)),
			};
			_SortOrder = SortOrders[0];
			if (!string.IsNullOrEmpty(SettingsFile.Instance.LastSortOrder)) {
				foreach (var order in SortOrders)
					if (order.Name == SettingsFile.Instance.LastSortOrder) {
						_SortOrder = order;
						break;
					}
			}

			this.WhenAnyValue(vm => vm.FilterByPath)
					.Throttle(TimeSpan.FromMilliseconds(500), RxSchedulers.MainThreadScheduler)
						.Subscribe(_ => { RebuildSearchPathIndex(); view?.Refresh(); });
		}

		void Duplicates_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) {
			if (e.OldItems != null) {
				foreach (INotifyPropertyChanged item in e.OldItems) {
					item.PropertyChanged -= DuplicateItemVM_PropertyChanged;
					if (((DuplicateItemVM)item).Checked) {
						DuplicatesCheckedCounter--;
						DuplicatesCheckedSizeInternal -= CheckedSizeOf((DuplicateItemVM)item);
						AdjustCheckedGroupIndex((DuplicateItemVM)item, -1);
					}
				}
			}
			if (e.NewItems != null) {
				foreach (INotifyPropertyChanged item in e.NewItems) {
					item.PropertyChanged += DuplicateItemVM_PropertyChanged;
					// Items can arrive already checked (restored backups); count them
					// so the counters and the per-group index stay accurate.
					if (((DuplicateItemVM)item).Checked) {
						DuplicatesCheckedCounter++;
						DuplicatesCheckedSizeInternal += CheckedSizeOf((DuplicateItemVM)item);
						AdjustCheckedGroupIndex((DuplicateItemVM)item, +1);
					}
				}
			}
			if (e.Action == NotifyCollectionChangedAction.Reset) {
				DuplicatesCheckedCounter = 0;
				DuplicatesCheckedSizeInternal = 0;
				checkedCountByGroup.Clear();
				selectionUndoStack.Clear();
			}
		}

		void DuplicateItemVM_PropertyChanged(object? sender, PropertyChangedEventArgs e) {
			if (e.PropertyName != nameof(DuplicateItemVM.Checked) || sender == null) return;
			RecordCheckedChangeForUndo((DuplicateItemVM)sender);
			if (((DuplicateItemVM)sender).Checked) {
				DuplicatesCheckedCounter++;
				DuplicatesCheckedSizeInternal += CheckedSizeOf((DuplicateItemVM)sender);
				AdjustCheckedGroupIndex((DuplicateItemVM)sender, +1);
			}
			else {
				DuplicatesCheckedCounter--;
				DuplicatesCheckedSizeInternal -= CheckedSizeOf((DuplicateItemVM)sender);
				AdjustCheckedGroupIndex((DuplicateItemVM)sender, -1);
			}
		}

		public async void Thumbnails_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e) {
			bool isReadyToCompare = IsGathered;
			isReadyToCompare &= Scanner.Settings.ThumbnailCount == e.NewValue;
			if (!isReadyToCompare && ApplicationHelpers.MainWindowDataContext.IsReadyToCompare)
				await MessageBoxService.Show($"Number of thumbnails can't be changed between quick rescans. Full scan will be required.");
			ApplicationHelpers.MainWindowDataContext.IsReadyToCompare = isReadyToCompare;
		}

		private void Scanner_ThumbnailProgress(int arg1, int arg2) => Dispatcher.UIThread.Post(() => {
			ThumbnailRetrievalProgressText = $"Retrieving thumbnails for preview: {arg1}/{arg2}";
		});

		void Scanner_ThumbnailsRetrieved(object? sender, EventArgs e) {
			//Reset properties
			ScanProgressText = string.Empty;
			RemainingTime = new TimeSpan().Format();
			ScanProgressValue = 0;
			ScanProgressMaxValue = 100;
			ThumbnailRetrievalProgressText = string.Empty;
			ShowThumbnailRetrievalProgressBar = false;
			// Keep the segments: the bars are a persistent dashboard now and reappear
			// automatically when the results list empties.
			ShowCompareText = false;
			UpdateDriveBarVisibility();
#pragma warning disable CS4014
			if (SettingsFile.Instance.BackupAfterListChanged)
				ExportScanResults(BackupScanResultsFile);
#pragma warning restore CS4014
		}

		void Scanner_FilesEnumerated(object? sender, EventArgs e) => ChangeIsBusyMessage();

		async void Scanner_DatabaseCleaned(object? sender, EventArgs e) {
			IsBusy = false;
			await MessageBoxService.Show(App.Lang["Message.DatabaseCleaned"]);
		}

		public async Task<bool> SaveScanResults() {
			if (Duplicates.Count == 0 || !SettingsFile.Instance.AskToSaveResultsOnExit) {
				try { Utils.ThumbCacheHelpers.Provider?.FlushIndex(); } catch { }
				return true;
			}
			MessageBoxButtons? result = await MessageBoxService.Show(App.Lang["Message.SaveResultsPrompt"],
				MessageBoxButtons.Yes | MessageBoxButtons.No | MessageBoxButtons.Cancel);
			if (result == null || result == MessageBoxButtons.Cancel) {
				return false;
			}
			if (result != MessageBoxButtons.Yes) {
				return true;
			}
			await ExportScanResults(BackupScanResultsFile);
			return true;
		}

		public void RestoreBackupScanResults() {
			if (File.Exists(BackupScanResultsFile))
				ImportScanResultsIncludingThumbnails(BackupScanResultsFile);
		}

		public async void LoadDatabase() {
			IsBusy = true;
			IsBusyOverlayText = "Loading database...";
			bool success = await ScanEngine.LoadDatabase();
			IsBusy = false;
			if (!success) {
				await MessageBoxService.Show(App.Lang["Message.LoadDatabaseFailed"]);
				Environment.Exit(-1);
			}
			// The directory tab is the startup tab, so its tree may have indexed an empty DB while
			// this load was still running — re-snapshot now that the real database is in memory.
			RefreshDirectoryTree();
			// Pre-scan drive preview: draw the per-drive bars from cumulative DB state right
			// away, so the pause checkboxes and worker caps can be configured BEFORE starting
			// a stage (they were previously reachable only once a scan emitted progress).
			SyncCoreSettings();
			var preview = await System.Threading.Tasks.Task.Run(Scanner.GetDrivePreview);
			UpdateDriveSegments(preview);
		}

		void CheckScheduledScan() {
			if (!SettingsFile.Instance.EnableScheduledScan || IsScanning || scheduledScanInProgress)
				return;
			if (!TryParseScheduledTime(SettingsFile.Instance.ScheduledScanTime, out var scheduledTime)) {
				if (!scheduleTimeInvalidNotified) {
					Logger.Instance.Info(App.Lang["Log.InvalidScheduledScanTime"]);
					scheduleTimeInvalidNotified = true;
				}
				return;
			}
			scheduleTimeInvalidNotified = false;
			var now = DateTime.Now;
			if (lastScheduledScanDate.Date == now.Date)
				return;
			if (now.TimeOfDay < scheduledTime)
				return;
			TryStartScheduledScan();
		}

		static bool TryParseScheduledTime(string value, out TimeSpan time) {
			time = default;
			if (string.IsNullOrWhiteSpace(value)) return false;
			if (TimeSpan.TryParse(value, out time))
				return true;
			return TimeSpan.TryParseExact(value, "hh\\:mm", null, out time);
		}

		void TryStartScheduledScan() {
			if (IsScanning || IsBusy) return;
			if (!string.IsNullOrEmpty(SettingsFile.Instance.CustomDatabaseFolder) && !Directory.Exists(SettingsFile.Instance.CustomDatabaseFolder)) {
				Logger.Instance.Info(App.Lang["Log.ScheduledScanSkippedMissingDatabaseFolder"]);
				return;
			}
			if (Duplicates.Count > 0) {
				Logger.Instance.Info(App.Lang["Log.ScheduledScanSkippedWithResults"]);
				return;
			}
			if ((SettingsFile.Instance.UseNativeFfmpegBinding && !ScanEngine.NativeFFmpegExists) ||
				(!SettingsFile.Instance.UseNativeFfmpegBinding && !ScanEngine.FFmpegExists)) {
				Logger.Instance.Info(App.Lang["Log.ScheduledScanSkippedMissingFfmpeg"]);
				return;
			}
			if (!ScanEngine.FFprobeExists) {
				Logger.Instance.Info(App.Lang["Log.ScheduledScanSkippedMissingFfprobe"]);
				return;
			}
			if (SettingsFile.Instance.Includes.Count == 0) {
				Logger.Instance.Info(App.Lang["Log.ScheduledScanSkippedNoFolders"]);
				return;
			}
			scheduledScanInProgress = true;
			lastScheduledScanDate = DateTime.Now.Date;
			Dispatcher.UIThread.Post(() => StartScanCommand.Execute("FullScan").Subscribe());
		}

		void Scanner_Progress(object? sender, ScanProgressChangedEventArgs e) =>
			Dispatcher.UIThread.InvokeAsync(() => {
				// Stage goes at the END — the status bar's TextBlock uses PrefixCharacterEllipsis,
				// so trimming happens at the start (leading directory path) and the filename + stage stay visible.
				string stageSuffix = string.IsNullOrEmpty(e.CurrentStage)
					? string.Empty
					: e.StageMax > 0 ? $"  [{e.CurrentStage} {e.StageCurrent}/{e.StageMax}]" : $"  [{e.CurrentStage}]";
				ScanProgressText = e.CurrentFile + stageSuffix;
				RemainingTime = e.Remaining.Format();
				ScanProgressValue = e.CurrentPosition;
				ScanProgressCount = $"{e.CurrentPosition:N0} / {e.MaxPosition:N0}";
					ShowScanProgressBar = true;
				TimeElapsed = e.Elapsed.Format();
				ScanProgressMaxValue = e.MaxPosition;
				UpdateDriveSegments(e.Drives);
				// Phase-level pushes (stage set, no file payload) take over the busy overlay's
				// subtitle: a blocking phase like "disk layout ordering" is otherwise visible only
				// in the per-drive rows at the bottom, and with the centre of the window still
				// showing a random quip the app reads as frozen. While the phase is active its
				// per-file events update the subtitle with live N/M; the phase's closing empty
				// push restores the quip. Pause/stop texts own the overlay, hence the guard.
				if (!IsPaused && !IsPauseDraining) {
					if (string.IsNullOrEmpty(e.CurrentFile)) {
						if (!string.IsNullOrEmpty(e.CurrentStage)) overlayStageActive = true;
						else if (overlayStageActive) {
							overlayStageActive = false;
							ChangeIsBusyMessage();
						}
					}
					if (overlayStageActive && !string.IsNullOrEmpty(e.CurrentStage))
						IsBusyOverlayText = e.StageMax > 0
							? $"{e.CurrentStage}  {e.StageCurrent:N0}/{e.StageMax:N0}"
							: $"{e.CurrentStage}…";
				}
				if (IsPauseDraining) {
					// Each parking worker fires an unthrottled snapshot; the last one carries zero
					// active rows → everything safely parked, switch "pausing…" to "paused".
					int active = e.Drives?.Sum(d => d.ActiveFiles?.Length ?? 0) ?? 0;
					if (active == 0 && IsPaused) {
						IsPauseDraining = false;
						BusyHeadingText = App.Lang["Busy.Paused.Title"];
						IsBusyOverlayText = App.Lang["Busy.Paused.Detail"];
					}
				}
			});

		// The scan progress denominator (MaxPosition) counts in-scope DB entries this run never
		// processes — paused-drive entries, out-of-scope skips — so the live counter tops out a
		// few % short (~97%). On a genuine finish, snap the bar to full so it reads 100%, hold a
		// beat, then hide it. Not called on abort/stop (those didn't complete).
		async void FlashScanCompleteThenHide() {
			ScanProgressValue = ScanProgressMaxValue;
			await Task.Delay(1000);
			if (!IsScanning)   // a new scan may have started during the hold — don't hide its bar
				ShowScanProgressBar = false;
		}

		void Scanner_ScanAborted(object? sender, EventArgs e) =>
			Dispatcher.UIThread.InvokeAsync(() => {
				IsScanning = false;
				IsBusy = false;
				IsReadyToCompare = false;
				IsGathered = false;
				scheduledScanInProgress = false;
				stageOnlyRunInProgress = false;
				ShowCompareText = false;
				UpdateDriveBarVisibility();
			});

		// A full scan fires BuildingHashesDone too (right before it chains into compare) — only a
		// stage-only run may reset the busy state here, otherwise the busy overlay would vanish
		// while the full scan is still comparing.
		void Scanner_BuildingHashesDone(object? sender, EventArgs e) =>
			Dispatcher.UIThread.InvokeAsync(() => {
				if (!stageOnlyRunInProgress)
					return;
				stageOnlyRunInProgress = false;
				IsScanning = false;
				IsBusy = false;
				// File-list building ignores the pause token, so a stage run can complete while
				// paused — clear it or the next scan starts with Resume/Pause swapped.
				IsPaused = false;
				IsPauseDraining = false;
				IsReadyToCompare = true;
				IsGathered = true;
				ScanProgressText = string.Empty;
				RemainingTime = TimeSpan.Zero.Format();
				ScanProgressValue = 0;
				ShowCompareText = false;
				UpdateDriveBarVisibility();   // gather-only run: bars stay (results list untouched)
					FlashScanCompleteThenHide();   // hold the bar at 100% for a beat, then hide it
				RefreshDirectoryTree();   // directory-selection tab: DB changed, refresh unscanned counts
			});

		void Scanner_ScanDone(object? sender, EventArgs e) =>
			Dispatcher.UIThread.InvokeAsync(() => {
				IsScanning = false;
				IsBusy = false;
				IsReadyToCompare = true;
				IsGathered = true;
				ScanProgressText = string.Empty;
				RemainingTime = TimeSpan.Zero.Format();
				ScanProgressValue = 0;
				RefreshDirectoryTree();   // directory-selection tab: DB changed, refresh unscanned counts
				ShowCompareText = false;
				// Bars hide only when duplicates actually land in the list below (the
				// Duplicates.CollectionChanged hook re-evaluates on every add).
				FlashScanCompleteThenHide();   // hold the bar at 100% for a beat, then hide it

					var completedScheduledScan = scheduledScanInProgress;
				scheduledScanInProgress = false;
				stageOnlyRunInProgress = false;

				var blacklistedGids = ComputeBlacklistedGroupIds(
					Scanner.Duplicates.Select(d => (d.GroupId, d.Path, ScanEngine.GetOsHash(d.Path))));
				if (blacklistedGids.Count > 0)
					Scanner.Duplicates.RemoveWhere(d => blacklistedGids.Contains(d.GroupId));

				foreach (var item in Scanner.Duplicates)
					Duplicates.Add(new DuplicateItemVM(item));

				if (SettingsFile.Instance.GeneratePreviewThumbnails) {
					ShowThumbnailRetrievalProgressBar = true;
					ThumbnailRetrievalProgressText = "Starting to retrieve thumbnails for preview";
					Scanner.RetrieveThumbnails();
				}

				BuildDuplicatesView();
				RebuildSearchPathIndex();
				RefreshGroupStats();

				if (SettingsFile.Instance.AutoApplySelectionPresetEnabled &&
					!string.IsNullOrEmpty(SettingsFile.Instance.AutoApplySelectionPreset)) {
					var preset = SettingsFile.Instance.CustomSelectionPresets
						.FirstOrDefault(p => p.Name == SettingsFile.Instance.AutoApplySelectionPreset);
					if (preset != null) {
						RunCustomSelection(preset.Data);
						Logger.Instance.Info(string.Format(App.Lang["Log.AutoAppliedPreset"], preset.Name));
					}
					else {
						Logger.Instance.Info(string.Format(App.Lang["Log.AutoApplyPresetMissing"], SettingsFile.Instance.AutoApplySelectionPreset));
					}
				}

				AutoCheckTombstoneMatches();
				CollapseAllTombstoneGroups();

				if (completedScheduledScan && SettingsFile.Instance.NotifyOnScheduledScanComplete) {
					_ = MessageBoxService.Show(App.Lang["Message.ScheduledScanCompleted"]);
				}

				if (SettingsFile.Instance.NotifyOnScanComplete) {
					VDF.GUI.Utils.DesktopNotificationHelper.Notify(
						App.Lang["Notification.ScanComplete.Title"],
						string.Format(App.Lang["Notification.ScanComplete.Message"], TotalDuplicateGroups));
				}
			});

		// Groups that contain a tombstone (a fingerprint of content the user already deleted) mean any
		// LIVE member is a re-download of rejected content -- pre-check it for deletion so the user only
		// has to confirm. Offline members (unplugged drive) are shown but never targeted. TOMBSTONE-DESIGN.md.
		void AutoCheckTombstoneMatches() {
			var tombstoneGroups = Duplicates
				.Where(d => d.IsTombstone)
				.Select(d => d.ItemInfo.GroupId)
				.ToHashSet();
			if (tombstoneGroups.Count == 0)
				return;
			int autoChecked = 0;
			foreach (var d in Duplicates)
				if (!d.IsTombstone && !d.IsOffline &&
					tombstoneGroups.Contains(d.ItemInfo.GroupId) &&
					File.Exists(d.ItemInfo.Path)) {
					d.Checked = true;
					autoChecked++;
				}
			if (autoChecked > 0)
				Logger.Instance.Info($"Auto-checked {autoChecked} re-download(s) matching previously deleted content.");
		}

		// A group whose members are ALL tombstones (files gone, drive mounted) has no live re-download
		// to act on -- pure noise in the list. Keep ONE fingerprint (still catches a future re-download)
		// and drop the redundant rest from the DB, then remove the whole group from the results. Groups
		// with any offline member (drive unplugged) are left alone -- the drive may return. This is the
		// deferred "dedupe multiple tombstones of the same content" item in TOMBSTONE-DESIGN.md.
		void CollapseAllTombstoneGroups() {
			var rowsToDrop = new List<DuplicateItemVM>();
			int dbRemoved = 0;
			foreach (var group in Duplicates.GroupBy(d => d.ItemInfo.GroupId)) {
				var members = group.ToList();
				if (members.Count < 2 || !members.All(d => d.IsTombstone))
					continue;   // any live/offline member -> keep the group as-is
				foreach (var d in members.Skip(1))   // keep the first fingerprint, drop the rest
					if (ScanEngine.RemoveFromDatabase(new FileEntry { Path = d.ItemInfo.Path }))
						dbRemoved++;
				rowsToDrop.AddRange(members);   // whole group leaves the list (nothing actionable left)
			}
			if (rowsToDrop.Count == 0)
				return;
			var drop = new HashSet<DuplicateItemVM>(rowsToDrop, ReferenceEqualityComparer<DuplicateItemVM>.Instance);
			for (int i = Duplicates.Count - 1; i >= 0; i--)
				if (drop.Contains(Duplicates[i]))
					Duplicates.RemoveAt(i);
			ScanEngine.SaveDatabase();
			RefreshGroupStats();
			view?.Refresh();
			Logger.Instance.Info($"Collapsed all-tombstone group(s): removed {dbRemoved} redundant fingerprint(s), cleared {rowsToDrop.Count} list row(s).");
		}

		void BuildDuplicatesView() {
			view = new DataGridCollectionView(Duplicates);
			view.GroupDescriptions.Add(new DataGridPathGroupDescription($"{nameof(DuplicateItemVM.ItemInfo)}.{nameof(DuplicateItem.GroupId)}"));
			// Rebuilding the view (rescan, import) previously dropped the active sort
			// while the sort ComboBox kept displaying it.
			if (_SortOrder.Sort != null)
				view.SortDescriptions.Add(_SortOrder.Sort);
			view.Filter += DuplicatesFilter;
			GetDataGrid.ItemsSource = view;
			TotalSizeRemovedInternal = 0;
		}

		static DataGrid GetDataGrid => ApplicationHelpers.MainWindow.FindControl<DataGrid>("dataGridGrouping")!;

		void RefreshGroupStats() {
			TotalDuplicates = Duplicates.Count;
			int groupCount = 0;
			long totalSize = 0;
			long savings = 0;
			foreach (var group in Duplicates.GroupBy(x => x.ItemInfo.GroupId)) {
				groupCount++;
				long groupTotal = 0;
				long largest = 0;
				foreach (var item in group) {
					long size = Math.Max(0, item.ItemInfo.SizeLong);
					groupTotal += size;
					if (size > largest) largest = size;
				}
				totalSize += groupTotal;
				savings += groupTotal - largest;
			}
			TotalDuplicatesSize = totalSize.BytesToString();
			TotalDuplicateGroups = groupCount;
			PotentialSavings = savings.BytesToString();
		}

		private DuplicateItemVM? GetSelectedDuplicateItem() {
			return GetDataGrid.SelectedItem as DuplicateItemVM;
		}

		private List<DuplicateItemVM> GetSelectedDuplicates() {
			return GetDataGrid.SelectedItems?.Cast<DuplicateItemVM>().ToList() ?? new();
		}

		public static ReactiveCommand<Unit, Unit> LatestReleaseCommand => ReactiveCommand.CreateFromTask(async () => {
			try {
				Process.Start(new ProcessStartInfo {
					FileName = "https://github.com/0x90d/videoduplicatefinder/releases",
					UseShellExecute = true
				});
			}
			catch {
				await MessageBoxService.Show(App.Lang["Message.OpenReleaseFailed"]);
			}
		});

		public static ReactiveCommand<Unit, Unit> OpenOwnFolderCommand => ReactiveCommand.Create(() => {
			Process.Start(new ProcessStartInfo {
				FileName = CoreUtils.CurrentFolder,
				UseShellExecute = true,
			});
		});

		public ReactiveCommand<Unit, Unit> CleanDatabaseCommand => ReactiveCommand.Create(() => {
			IsBusy = true;
			IsBusyOverlayText = App.Lang["Busy.CleaningDatabase"];
			Scanner.CleanupDatabase();
		});

		// Removes "ghost" entries: file gone from a MOUNTED drive + no comparable fingerprint data
		// (tombstones — missing files WITH fingerprints — are untouched; offline drives too).
		// Count-first so the confirm dialog shows exactly what would be removed.
		public ReactiveCommand<Unit, Unit> PruneGhostEntriesCommand => ReactiveCommand.CreateFromTask(async () => {
			int count = await Task.Run(ScanEngine.CountGhostEntries);
			if (count == 0) {
				await MessageBoxService.Show(App.Lang["Message.NoGhostEntries"]);
				return;
			}
			MessageBoxButtons? dlgResult = await MessageBoxService.Show(
				string.Format(App.Lang["Message.PruneGhostEntriesConfirm"], count),
				MessageBoxButtons.Yes | MessageBoxButtons.No);
			if (dlgResult != MessageBoxButtons.Yes) return;
			int removed = await Task.Run(ScanEngine.PruneGhostEntries);
			await MessageBoxService.Show(string.Format(App.Lang["Message.PruneGhostEntriesDone"], removed));
		});

		public ReactiveCommand<Unit, Unit> ClearDatabaseCommand => ReactiveCommand.CreateFromTask(async () => {
			MessageBoxButtons? dlgResult = await MessageBoxService.Show(
				App.Lang["Message.ClearDatabaseWarning"],
				MessageBoxButtons.Yes | MessageBoxButtons.No);
			if (dlgResult != MessageBoxButtons.Yes) return;
			ScanEngine.ClearDatabase();
			await MessageBoxService.Show(App.Lang["Message.Done"]);
		});

		public static ReactiveCommand<Unit, Unit> EditDataBaseCommand => ReactiveCommand.CreateFromTask(async () => {
			DatabaseViewer dlg = new();
			bool res = await dlg.ShowDialog<bool>(ApplicationHelpers.MainWindow);
		});
		public static ReactiveCommand<Unit, Unit> ImportDataBaseFromJsonCommand => ReactiveCommand.CreateFromTask(async () => {
			var result = await Utils.PickerDialogUtils.OpenFilePicker(new FilePickerOpenOptions() {
				SuggestedStartLocation = await ApplicationHelpers.MainWindow.StorageProvider.TryGetFolderFromPathAsync(CoreUtils.CurrentFolder),
				FileTypeFilter = [new FilePickerFileType("Json File") { Patterns = ["*.json"] }]
			});
			if (string.IsNullOrEmpty(result)) return;

			bool success = ScanEngine.ImportDataBaseFromJson(result, new JsonSerializerOptions {
				IncludeFields = true,
			});
			if (!success)
				await MessageBoxService.Show(App.Lang["Message.ImportDatabaseFailed"]);
			else
				ScanEngine.SaveDatabase();
		});

		public static ReactiveCommand<Unit, Unit> ExportDataBaseToJsonCommand => ReactiveCommand.Create(() => {
			ExportDbToJson(new JsonSerializerOptions {
				IncludeFields = true,
			});
		});

		public static ReactiveCommand<Unit, Unit> ExportDataBaseToJsonPrettyCommand => ReactiveCommand.Create(() => {
			ExportDbToJson(new JsonSerializerOptions {
				IncludeFields = true,
				WriteIndented = true,
			});
		});

		async static void ExportDbToJson(JsonSerializerOptions options) {
			var result = await Utils.PickerDialogUtils.SaveFilePicker(new FilePickerSaveOptions() {
				DefaultExtension = ".json",
				FileTypeChoices = [new FilePickerFileType("Json Files") { Patterns = ["*.json"] }]
			});
			if (string.IsNullOrEmpty(result)) return;

			if (!ScanEngine.ExportDataBaseToJson(result, options))
				await MessageBoxService.Show(App.Lang["Message.ExportDatabaseFailed"]);
		}

		public ReactiveCommand<Unit, Unit> ExportScanResultsCommand => ReactiveCommand.CreateFromTask(async () => {
			await ExportScanResults();
		});

		public ReactiveCommand<Unit, Unit> ExportScanResultsPrettyCommand => ReactiveCommand.CreateFromTask(async () => {
			await ExportScanResults(envelopeTypeInfo: GuiJsonFieldsPrettyContext.Default.ScanResultsEnvelope);
		});

		public ReactiveCommand<Unit, Unit> ExportScanResultsToFileCommand => ReactiveCommand.CreateFromTask(async () => {
			await ExportScanResults();
		});

		public ReactiveCommand<Unit, Unit> ExportScanResultsToCsvCommand => ReactiveCommand.CreateFromTask(async () => {
			if (Duplicates.Count == 0) return;
			var path = await Utils.PickerDialogUtils.SaveFilePicker(new FilePickerSaveOptions() {
				DefaultExtension = ".csv",
				FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }]
			});
			if (string.IsNullOrEmpty(path)) return;
			try {
				var snapshot = Duplicates.ToList();
				await Task.Run(() => WriteScanResultsCsv(path, snapshot));
			}
			catch (Exception ex) {
				string error = string.Format(App.Lang["Message.ExportScanResultsFailed"], ex);
				Logger.Instance.Info(error);
				await MessageBoxService.Show(error);
			}
		});

		static void WriteScanResultsCsv(string path, List<DuplicateItemVM> items) {
			static string Escape(string? s) {
				s ??= string.Empty;
				return s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r')
					? "\"" + s.Replace("\"", "\"\"") + "\""
					: s;
			}
			var inv = System.Globalization.CultureInfo.InvariantCulture;
			// UTF-8 BOM so Excel detects the encoding.
			using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
			writer.WriteLine("GroupId,Path,SizeBytes,Duration,Resolution,Fps,BitrateKbs,AudioFormat,AudioSampleRate,Similarity,DateCreated,IsImage,Checked");
			// Keep group members on adjacent rows regardless of list order.
			foreach (var group in items.GroupBy(i => i.ItemInfo.GroupId))
				foreach (var item in group) {
					var info = item.ItemInfo;
					writer.WriteLine(string.Join(',',
						info.GroupId.ToString(),
						Escape(info.Path),
						info.SizeLong.ToString(inv),
						info.Duration.ToString(null, inv),
						Escape(info.FrameSize),
						info.Fps.ToString(inv),
						info.BitRateKbs.ToString(inv),
						Escape(info.AudioFormat),
						info.AudioSampleRate.ToString(inv),
						info.Similarity.ToString(inv),
						info.DateCreated.ToString("yyyy-MM-dd HH:mm:ss", inv),
						info.IsImage.ToString(),
						item.Checked.ToString()));
				}
		}

		// Accepts both the v1 envelope ({version, items}) and the legacy raw array
		// shape produced by older builds. Returns the items list.
		private static List<DuplicateItemVM> ReadScanResultsItems(JsonElement root) {
			if (root.ValueKind == JsonValueKind.Array)
				return root.Deserialize(GuiJsonFieldsContext.Default.ListDuplicateItemVM) ?? new();

			if (root.ValueKind == JsonValueKind.Object &&
				root.TryGetProperty("items", out var itemsEl) &&
				itemsEl.ValueKind == JsonValueKind.Array) {
				return itemsEl.Deserialize(GuiJsonFieldsContext.Default.ListDuplicateItemVM) ?? new();
			}

			throw new JsonException("Unknown scan results format");
		}

		async Task ExportScanResults(string? path = null, bool includeThumbnails = true, int thumbMaxEdge = 160, System.Text.Json.Serialization.Metadata.JsonTypeInfo<ScanResultsEnvelope>? envelopeTypeInfo = null) {
			path ??= await Utils.PickerDialogUtils.SaveFilePicker(new FilePickerSaveOptions() {
				SuggestedStartLocation = await ApplicationHelpers.MainWindow.StorageProvider.TryGetFolderFromPathAsync(CoreUtils.CurrentFolder),
				DefaultExtension = includeThumbnails ? ".zip" : ".json",
				FileTypeChoices = [new FilePickerFileType("Scan Results") { Patterns = [includeThumbnails ? "*.zip" : ".scanresults"] }]
			});

			if (string.IsNullOrEmpty(path)) return;

			IsBusy = true;
			IsBusyOverlayText = App.Lang["Busy.SavingScanResults"];
			var dir = Path.GetDirectoryName(path)!;
			var tmp = Path.Combine(dir, Path.GetFileName(path) + ".tmp");

			try {
				var snapshot = Duplicates.ToList();
				var envelope = new ScanResultsEnvelope { Version = ScanResultsEnvelope.CurrentVersion, Items = snapshot };

				if (!includeThumbnails) {
					await using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
					await JsonSerializer.SerializeAsync(fs, envelope, envelopeTypeInfo ?? GuiJsonFieldsContext.Default.ScanResultsEnvelope);
				}
				else {
					await using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
					using var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false);
					var jsonEntry = zip.CreateEntry("scan.json", CompressionLevel.NoCompression);

					await using (var es = jsonEntry.Open()) {
						await JsonSerializer.SerializeAsync(es, envelope, envelopeTypeInfo ?? GuiJsonFieldsContext.Default.ScanResultsEnvelope);
						await es.FlushAsync();
					}

					Utils.ThumbCacheHelpers.Provider?.FlushIndex();

					if (TempDirectory != null) {
						var packPath = Path.Combine(TempDirectory.Path, "thumbs.pack");
						var idxPath = Path.Combine(TempDirectory.Path, "thumbs.idx");

						if (File.Exists(packPath) && Utils.ThumbCacheHelpers.Provider != null) {
							var packEntry = zip.CreateEntry("thumbs.pack", CompressionLevel.NoCompression);
							using var es = packEntry.Open();
							Utils.ThumbCacheHelpers.Provider.CopyTo(es);
						}

						if (File.Exists(idxPath)) {
							var idxEntry = zip.CreateEntry("thumbs.idx", CompressionLevel.NoCompression);
							using var es = idxEntry.Open();
							using var fs2 = File.OpenRead(idxPath);
							fs2.CopyTo(es);
						}
					}
				}

				File.Move(tmp, path, overwrite: true);
			}
			catch (Exception ex) {
				IsBusy = false;
				string error = string.Format(App.Lang["Message.ExportScanResultsFailed"], ex);
				Logger.Instance.Info(error);
				await MessageBoxService.Show(error);
			}
			finally {
				try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
				IsBusy = false;
			}
		}

		public ReactiveCommand<Unit, Unit> ImportScanResultsFromFileCommand => ReactiveCommand.CreateFromTask(async () => {
			var result = await Utils.PickerDialogUtils.OpenFilePicker(new FilePickerOpenOptions {
				SuggestedStartLocation = await ApplicationHelpers.MainWindow.StorageProvider.TryGetFolderFromPathAsync(CoreUtils.CurrentFolder),
				FileTypeFilter = [new FilePickerFileType("Scan Results") { Patterns = ["*.zip"] }]
			});
			if (string.IsNullOrEmpty(result)) return;
			ImportScanResultsIncludingThumbnails(result);
		});

		async void ImportScanResultsIncludingThumbnails(string? path = null) {
			if (Duplicates.Count > 0) {
				MessageBoxButtons? result = await MessageBoxService.Show(App.Lang["Message.ImportScanResultsClearConfirm"], MessageBoxButtons.Yes | MessageBoxButtons.No);
				if (result != MessageBoxButtons.Yes) return;
			}

			if (path == null) {
				path = await Utils.PickerDialogUtils.OpenFilePicker(new FilePickerOpenOptions() {
					SuggestedStartLocation = await ApplicationHelpers.MainWindow.StorageProvider.TryGetFolderFromPathAsync(CoreUtils.CurrentFolder),
					FileTypeFilter = [new FilePickerFileType("Scan Results") { Patterns = ["*.zip"] }]
				});
			}
			if (string.IsNullOrEmpty(path)) return;

			try {
				using var stream = File.OpenRead(path);
				IsBusy = true;
				IsBusyOverlayText = App.Lang["Busy.ImportScanResults"];

				using var zip = ZipFile.OpenRead(path);
				var json = zip.GetEntry("scan.json") ?? throw new Exception("scan.json missing");
				// Parse and deserialize on a worker thread — large backups froze the UI (#789).
				List<DuplicateItemVM> items = await Task.Run(() => {
					using var js = json.Open();
					using var doc = JsonDocument.Parse(js);
					return ReadScanResultsItems(doc.RootElement);
				});

				int skipped = items.RemoveAll(it => it?.ItemInfo == null);
				if (skipped > 0)
					Logger.Instance.Info($"Skipped {skipped} corrupt scan result entries (missing ItemInfo)");
				if (items.Count == 0)
					throw new JsonException("All scan result entries were corrupt");

				// Apply not-a-match blacklist; saved results may pre-date marks made just before a crash.
				var importBlacklistedGids = ComputeBlacklistedGroupIds(
					items.Select(i => (i.ItemInfo.GroupId, i.ItemInfo.Path, ScanEngine.GetOsHash(i.ItemInfo.Path))));
				if (importBlacklistedGids.Count > 0) {
					int removed = items.RemoveAll(i => importBlacklistedGids.Contains(i.ItemInfo.GroupId));
					if (removed > 0)
						Logger.Instance.Info($"Filtered out {removed} items in {importBlacklistedGids.Count} blacklisted groups during import");
				}

				TempDirectory = TempExtractionManager.Register(new("VDF-"));

				// Validate ZIP entry paths to prevent path traversal (Zip Slip)
				foreach (var entryName in new[] { "thumbs.pack", "thumbs.idx" }) {
					var entry = zip.GetEntry(entryName);
					if (entry == null) continue;
					string dest = Path.GetFullPath(Path.Combine(TempDirectory.Path, entryName));
					if (!dest.StartsWith(Path.GetFullPath(TempDirectory.Path) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
						throw new InvalidOperationException($"ZIP entry '{entryName}' would extract outside target directory");
					entry.ExtractToFile(dest, true);
				}

				Utils.ThumbCacheHelpers.SetActiveProvider(Utils.ThumbPack.Open(TempDirectory.Path));

				Duplicates.Clear();
				foreach (var item in items)
					Duplicates.Add(item);

				BuildDuplicatesView();
				RefreshGroupStats();
				IsBusy = false;
				stream.Close();

				await PromptLoadMissingThumbnails();
			}
			catch (JsonException) {
				IsBusy = false;
				string error = App.Lang["Message.ImportScanResultsCorrupt"];
				Logger.Instance.Info(error);
				await MessageBoxService.Show(error);
			}
			catch (Exception ex) {
				IsBusy = false;
				string error = string.Format(App.Lang["Message.ImportScanResultsFailed"], ex);
				Logger.Instance.Info(error);
				await MessageBoxService.Show(error);
			}
		}

		/// <summary>
		/// A restored item is considered missing its preview when no usable thumbnail is cached
		/// for it — either it never got a key (thumbnails hadn't loaded before the backup was
		/// saved) or the pack has no non-empty entry for that key.
		/// </summary>
		static bool IsThumbnailMissing(DuplicateItemVM item) {
			if (string.IsNullOrEmpty(item.ThumbnailKey)) return true;
			var provider = Utils.ThumbCacheHelpers.Provider;
			if (provider == null) return true;
			return !provider.TryGetEntry(item.ThumbnailKey, out _, out var len) || len <= 0;
		}

		/// <summary>
		/// After restoring a saved scan that was interrupted before all thumbnails loaded, offer
		/// to generate the missing previews in one pass instead of leaving rows blank (issue #775).
		/// </summary>
		async Task PromptLoadMissingThumbnails() {
			var missing = Duplicates.Where(IsThumbnailMissing).ToList();
			if (missing.Count == 0) return;

			MessageBoxButtons? result = await MessageBoxService.Show(
				string.Format(App.Lang["Message.LoadMissingThumbnailsPrompt"], missing.Count),
				MessageBoxButtons.Yes | MessageBoxButtons.No);
			if (result != MessageBoxButtons.Yes) return;

			IsBusy = true;
			IsBusyOverlayText = App.Lang["Busy.LoadMissingThumbnails"];
			try {
				SyncCoreSettings();
				await Scanner.RetrieveThumbnailsForItems(missing.Select(d => d.ItemInfo));
			}
			finally {
				IsBusy = false;
			}
		}

		public ReactiveCommand<DuplicateItemVM, Unit> OpenItemCommand => ReactiveCommand.Create<DuplicateItemVM>(currentItem => {
			OpenItems();
		});

		public ReactiveCommand<Unit, Unit> OpenItemsByColIdCommand => ReactiveCommand.Create(() => {
			var tag = GetDataGrid.CurrentColumn?.Tag as string;
			if (tag == "Thumbnail")
				OpenItems();
			else if (tag == "Path")
				OpenItemsInFolder();
		});

		public ReactiveCommand<Unit, Unit> ThumbnailDoubleClickCommand => ReactiveCommand.Create(() => {
			if (SettingsFile.Instance.ThumbnailDoubleClickAction == Data.ThumbnailDoubleClickAction.OpenThumbnailComparer)
				ShowGroupInThumbnailComparerCommand.Execute().Subscribe();
			else
				OpenItems();
		});

		public Data.ThumbnailDoubleClickOption[] ThumbnailDoubleClickOptions { get; } = {
			new(App.Lang["MainWindow.Settings.ThumbnailDoubleClick.OpenFile"], Data.ThumbnailDoubleClickAction.OpenFile),
			new(App.Lang["MainWindow.Settings.ThumbnailDoubleClick.OpenThumbnailComparer"], Data.ThumbnailDoubleClickAction.OpenThumbnailComparer),
		};

		public ReactiveCommand<Unit, Unit> OpenItemInFolderCommand => ReactiveCommand.Create(OpenItemsInFolder);

		public ReactiveCommand<string, Unit> OpenGroupCommand => ReactiveCommand.Create<string>(openInFolder => {
			if (GetSelectedDuplicateItem() is DuplicateItemVM currentItem) {
				List<DuplicateItemVM> items = Duplicates.Where(d => d.ItemInfo.GroupId == currentItem.ItemInfo.GroupId).ToList();
				if (openInFolder == "0")
					AlternativeOpen(String.Empty, SettingsFile.Instance.CustomCommands.OpenMultiple, items);
				else
					AlternativeOpen(String.Empty, SettingsFile.Instance.CustomCommands.OpenMultipleInFolder, items);
			}
		});

		public async void OpenItems() {
			if (AlternativeOpen(SettingsFile.Instance.CustomCommands.OpenItem,
								SettingsFile.Instance.CustomCommands.OpenMultiple))
				return;

			if (GetSelectedDuplicateItem() is not DuplicateItemVM currentItem) return;
			try {
				if (CoreUtils.IsWindows) {
					Process.Start(new ProcessStartInfo {
						FileName = currentItem.ItemInfo.Path,
						UseShellExecute = true
					});
				}
				else {
					Process.Start(new ProcessStartInfo {
						FileName = currentItem.ItemInfo.Path,
						UseShellExecute = true,
						Verb = "open"
					});
				}
			}
			catch (Exception ex) {
				await MessageBoxService.Show(string.Format(App.Lang["Message.OpenFilesFailed"], ex.Message));
				return;
			}
		}

		public async void OpenItemsInFolder() {
			if (AlternativeOpen(SettingsFile.Instance.CustomCommands.OpenItemInFolder,
								SettingsFile.Instance.CustomCommands.OpenMultipleInFolder))
				return;

			if (GetSelectedDuplicateItem() is not DuplicateItemVM currentItem) return;
			try {
				if (OperatingSystem.IsWindows()) {
					try {
						Utils.ShellUtils.ShowInExplorer(currentItem.ItemInfo.Path);
					}
					catch {
						// Fallback to explorer.exe if shell API fails (Notepad++/Electron pattern)
						var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
						psi.ArgumentList.Add($"/select,{currentItem.ItemInfo.Path}");
						Process.Start(psi);
					}
				}
				else if (OperatingSystem.IsMacOS()) {
					var psi = new ProcessStartInfo("open") { UseShellExecute = false };
					psi.ArgumentList.Add("-R");
					psi.ArgumentList.Add(currentItem.ItemInfo.Path);
					Process.Start(psi);
				}
				else {
					Process.Start(new ProcessStartInfo {
						FileName = currentItem.ItemInfo.Folder,
						UseShellExecute = true,
						Verb = "open"
					});
				}
			}
			catch (Exception ex) {
				await MessageBoxService.Show(string.Format(App.Lang["Message.OpenFilesFailed"], ex.Message));
				return;
			}
		}

		private bool AlternativeOpen(string cmdSingle, string cmdMulti, List<DuplicateItemVM>? items = null) {
			if (string.IsNullOrEmpty(cmdSingle) && string.IsNullOrEmpty(cmdMulti))
				return false;

			if (items == null) {
				items = new();
				if (!string.IsNullOrEmpty(cmdMulti)) {
					foreach (var selectedItem in GetSelectedDuplicates())
						if (selectedItem is DuplicateItemVM item)
							items.Add(item);
				}
				else {
					if (GetSelectedDuplicateItem() is DuplicateItemVM duplicateItem)
						items.Add(duplicateItem);
				}
			}

			string[]? cmd = null;
			string command = string.Empty;
			if (!string.IsNullOrEmpty(cmdSingle) && (string.IsNullOrEmpty(cmdMulti) || items.Count == 1))
				command = cmdSingle;
			else if (items.Count > 1)
				command = cmdMulti;
			if (!string.IsNullOrEmpty(command)) {
				if (command[0] == '"' || command[0] == '\'') {
					cmd = command.Split(command[0] + " ", 2);
					cmd[0] += command[0];
				}
				else
					cmd = command.Split(' ', 2);
			}
			if (string.IsNullOrEmpty(cmd?[0]))
				return false;

			command = cmd[0];
			var psi = new ProcessStartInfo {
				FileName = command,
				UseShellExecute = false,
				RedirectStandardError = true,
			};

			if (cmd.Length == 2 && cmd[1].Contains("%f")) {
				// When the user has a %f placeholder, substitute file paths into the argument template.
				// Each file gets its own copy of the template as a separate argument to avoid injection.
				foreach (var item in items) {
					psi.ArgumentList.Add(cmd[1].Replace("%f", item.ItemInfo.Path));
				}
			}
			else {
				if (cmd.Length == 2)
					psi.ArgumentList.Add(cmd[1]);
				foreach (var item in items)
					psi.ArgumentList.Add(item.ItemInfo.Path);
			}

			try {
				Process.Start(psi);
			}
			catch (Exception e) {
				Logger.Instance.Info(string.Format(App.Lang["Log.CustomCommandFailed"], command,
					string.Join(" ", psi.ArgumentList), e.Message));
			}

			return true;
		}

		public ReactiveCommand<Unit, Unit> RenameFileCommand => ReactiveCommand.CreateFromTask(async () => {
			if (GetSelectedDuplicateItem() is not DuplicateItemVM currentItem) return;
			if (!File.Exists(currentItem.ItemInfo.Path)) {
				await MessageBoxService.Show(App.Lang["Message.FileNoLongerExists"]);
				return;
			}
			var fi = new FileInfo(currentItem.ItemInfo.Path);
			Debug.Assert(fi.Directory != null, "fi.Directory != null");
			string newName = await InputBoxService.Show("Enter new name", Path.GetFileNameWithoutExtension(fi.FullName), title: "Rename File", readOnlyInfo: fi.FullName);
			if (string.IsNullOrEmpty(newName)) return;
			newName = FileUtils.SafePathCombine(fi.DirectoryName!, newName + fi.Extension);
			while (File.Exists(newName) && !string.Equals(fi.FullName, newName, StringComparison.OrdinalIgnoreCase)) {
				MessageBoxButtons? result = await MessageBoxService.Show($"A file with the name '{Path.GetFileName(newName)}' already exists. Do you want to overwrite this file? Click on 'No' to enter a new name", MessageBoxButtons.Yes | MessageBoxButtons.No | MessageBoxButtons.Cancel);
				if (result == null || result == MessageBoxButtons.Cancel)
					return;
				if (result == MessageBoxButtons.Yes)
					break;
				newName = await InputBoxService.Show("Enter new name", Path.GetFileNameWithoutExtension(newName), title: "Rename File", readOnlyInfo: fi.FullName);
				if (string.IsNullOrEmpty(newName))
					return;
				newName = FileUtils.SafePathCombine(fi.DirectoryName!, newName + fi.Extension);
			}
			try {
				ScanEngine.GetFromDatabase(currentItem.ItemInfo.Path, out var dbEntry);
				fi.MoveTo(newName, true);
				ScanEngine.UpdateFilePathInDatabase(newName, dbEntry!);
				currentItem.ItemInfo.Path = newName;
				ScanEngine.SaveDatabase();
			}
			catch (Exception e) {
				await MessageBoxService.Show(e.Message);
			}
		});

		public ReactiveCommand<Unit, Unit> ToggleCheckboxCommand => ReactiveCommand.Create(() => {
			using var _ = BeginSelectionUndoBatch();
			foreach (var item in GetSelectedDuplicates()) {
				if (item is not DuplicateItemVM currentItem) return;
				currentItem.Checked = !currentItem.Checked;
			}
		});

		static int MapToFfmpegMajor(int avcodecMajor, int avformatMajor, int avutilMajor) {
			int[] majors = { avcodecMajor, avformatMajor, avutilMajor };
			int want = 0;
			foreach (var m in majors) {
				int v = m switch {
					62 => 8,
					61 => 7,
					60 => 6,
					59 => 5,
					_ => 0
				};
				if (v > want) want = v;
			}
			return want;
		}

		static string ArchString(Architecture a) => a switch {
			Architecture.X64 => "x64 (64-bit)",
			Architecture.X86 => "x86 (32-bit)",
			Architecture.Arm64 => "arm64 (64-bit ARM)",
			Architecture.Arm => "arm (32-bit ARM)",
			_ => a.ToString()
		};

		public static string GetRequiredFfmpegPackage(string? vdfFolderPath = null) {
			int avcodec = ffmpeg.LIBAVCODEC_VERSION_MAJOR;
			int avformat = ffmpeg.LIBAVFORMAT_VERSION_MAJOR;
			int avutil = ffmpeg.LIBAVUTIL_VERSION_MAJOR;

			int ffMajor = MapToFfmpegMajor(avcodec, avformat, avutil);

			string platform =
				OperatingSystem.IsWindows() ? "Windows" :
				OperatingSystem.IsMacOS() ? "macOS" :
				OperatingSystem.IsLinux() ? "Linux" : "Unknown";

			var osArch = RuntimeInformation.OSArchitecture;
			var procArch = RuntimeInformation.ProcessArchitecture;
			string versionPart = ffMajor == 0 ? "unknown (old headers?)" : $"{ffMajor}.x";
			string archPart = ArchString(procArch);

			var msg =
$@"FFmpeg was not found.

Which FFmpeg you need:
  • Version: FFmpeg {versionPart}
  • Architecture: {archPart}

Platform/OS:
  • {platform} · OS arch: {ArchString(osArch)} · Process arch: {ArchString(procArch)}

Notes:
  • On ARM64 systems (e.g., Windows/macOS ARM): if your app runs as x64 (emulation/Rosetta), use x64 FFmpeg.
    If it runs natively as ARM64, use ARM64 FFmpeg.";

			if (OperatingSystem.IsWindows()) {
				string target = string.IsNullOrWhiteSpace(vdfFolderPath)
					? @"<YourApp>\VDF\bin"
					: System.IO.Path.Combine(vdfFolderPath, "bin");

				msg +=
	$@"

Windows setup:
  1) Create a folder named 'bin' inside your VDF folder:
       {target}
  2) Download the FFmpeg {versionPart} shared build for {archPart}.
  3) Extract these DLLs into that 'bin' folder:
       avcodec-*.dll, avformat-*.dll, avutil-*.dll, swresample-*.dll, swscale-*.dll";
			}
			else {
				msg +=
	@"

Non-Windows setup:
  • Recommended: Check Github instructions
  • Use the shared libraries matching the required version/architecture.
  • Make sure the dynamic loader can find them (e.g., alongside your app binary,
    via rpath, or environment variables like LD_LIBRARY_PATH/DYLD_LIBRARY_PATH).
  • Typical library names:
      - Linux: libavcodec.so.*, libavformat.so.*, libavutil.so.*, libswresample.so.*, libswscale.so.*
      - macOS: libavcodec.*.dylib, libavformat.*.dylib, libavutil.*.dylib, libswresample.*.dylib, libswscale.*.dylib";
			}

			return msg;
		}

		/// <summary>
		/// Message for the specific case where 'Use native FFmpeg binding' is on and the
		/// FFmpeg/FFprobe executables were found, but the matching shared libraries were not.
		/// The native binding cannot use the executable, so "FFmpeg was not found" is wrong and
		/// confusing here — guide the user to either disable native binding or install the libs.
		/// </summary>
		public static string GetNativeLibrariesMissingMessage() {
			int ffMajor = MapToFfmpegMajor(ffmpeg.LIBAVCODEC_VERSION_MAJOR, ffmpeg.LIBAVFORMAT_VERSION_MAJOR, ffmpeg.LIBAVUTIL_VERSION_MAJOR);
			string versionPart = ffMajor == 0 ? "FFmpeg" : $"FFmpeg {ffMajor}.x";
			string archPart = ArchString(RuntimeInformation.ProcessArchitecture);

			// Library file names are platform-specific but not translatable (filenames).
			string libNames = OperatingSystem.IsWindows()
				? "avcodec-*.dll, avformat-*.dll, avutil-*.dll, swresample-*.dll, swscale-*.dll"
				: OperatingSystem.IsMacOS()
					? "libavcodec.*.dylib, libavformat.*.dylib, libavutil.*.dylib, libswresample.*.dylib, libswscale.*.dylib"
					: "libavcodec.so.*, libavformat.so.*, libavutil.so.*, libswresample.so.*, libswscale.so.*";

			return string.Format(App.Lang["Message.NativeFfmpegLibrariesMissing"], versionPart, archPart, libNames);
		}

		public ReactiveCommand<string, Unit> StartScanCommand => ReactiveCommand.CreateFromTask(async (string command) => {
			if (!string.IsNullOrEmpty(SettingsFile.Instance.CustomDatabaseFolder) && !Directory.Exists(SettingsFile.Instance.CustomDatabaseFolder)) {
				await MessageBoxService.Show(App.Lang["Message.CustomDatabaseFolderMissing"]);
				return;
			}
			if (Duplicates.Count > 0) {
				if (await MessageBoxService.Show(App.Lang["Message.DiscardResultsPrompt"], MessageBoxButtons.Yes | MessageBoxButtons.No) != MessageBoxButtons.Yes) {
					return;
				}
			}

			if ((SettingsFile.Instance.UseNativeFfmpegBinding && !ScanEngine.NativeFFmpegExists) ||
				(!SettingsFile.Instance.UseNativeFfmpegBinding && !ScanEngine.FFmpegExists) ||
				!ScanEngine.FFprobeExists) {
				await DownloadSharedFfmpegAsync();
			}
			// Native binding on, shared libraries still missing, but the ffmpeg/ffprobe
			// executables ARE present: don't claim "FFmpeg was not found" — point the user
			// at the actual distinction (native needs shared libs) and the one-click way out
			// (disable native binding to use the executable). See issue #788.
			if (SettingsFile.Instance.UseNativeFfmpegBinding && !ScanEngine.NativeFFmpegExists &&
				ScanEngine.FFmpegExists && ScanEngine.FFprobeExists) {
				await MessageBoxService.Show(GetNativeLibrariesMissingMessage());
				return;
			}
			if ((SettingsFile.Instance.UseNativeFfmpegBinding && !ScanEngine.NativeFFmpegExists) ||
				(!SettingsFile.Instance.UseNativeFfmpegBinding && !ScanEngine.FFmpegExists)) {
				await MessageBoxService.Show(GetRequiredFfmpegPackage(CoreUtils.CurrentFolder));
				return;
			}
			if (!ScanEngine.FFprobeExists) {
				if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
					await MessageBoxService.Show(App.Lang["Message.FFprobeMissingWithHint"]);
				}
				else {
					await MessageBoxService.Show(App.Lang["Message.FFprobeMissing"]);
				}
				return;
			}
			if (SettingsFile.Instance.UseNativeFfmpegBinding && SettingsFile.Instance.HardwareAccelerationMode == Core.FFTools.FFHardwareAccelerationMode.auto) {
				await MessageBoxService.Show(App.Lang["Message.NativeFfmpegAutoNotSupported"]);
				return;
			}
			if (SettingsFile.Instance.Includes.Count == 0) {
				await MessageBoxService.Show(App.Lang["Message.NoScanFolders"]);
				return;
			}
			if (SettingsFile.Instance.MaxDegreeOfParallelism == 0) {
				await MessageBoxService.Show(App.Lang["Message.MaxDegreeOfParallelismInvalid"]);
				return;
			}
			if (SettingsFile.Instance.FilterByFileSize && SettingsFile.Instance.MaximumFileSize <= SettingsFile.Instance.MinimumFileSize) {
				await MessageBoxService.Show(App.Lang["Message.FileSizeFilterInvalid"]);
				return;
			}
			bool isFreshScan = true;
			Core.ScanStage? stageOnly = null;
			switch (command) {
			case "FullScan":
				isFreshScan = true;
				break;
			case "CompareOnly":
				isFreshScan = false;
				if (await MessageBoxService.Show(App.Lang["Message.RescanConfirm"], MessageBoxButtons.Yes | MessageBoxButtons.No) != MessageBoxButtons.Yes)
					return;
				break;
			case "StageFileList":
				stageOnly = Core.ScanStage.BuildFileList;
				break;
			case "StageGather":
				stageOnly = Core.ScanStage.GatherInfos;
				break;
			case "StageCompare":
				stageOnly = Core.ScanStage.Compare;
				break;
			case "StagePartialCompareIndex":
				SettingsFile.Instance.AudioCompareMethod = Core.AudioCompareMethod.InvertedIndex;
				goto case "StagePartialCompare";
			case "StagePartialCompareBrute":
				SettingsFile.Instance.AudioCompareMethod = Core.AudioCompareMethod.BruteForce;
				goto case "StagePartialCompare";
			case "StagePartialCompare":
				// The stage compares audio fingerprints, which stage 2 only extracts while
				// this setting is on — without it the run would silently find nothing.
				if (!SettingsFile.Instance.EnablePartialClipDetection) {
					await MessageBoxService.Show(App.Lang["Message.PartialStageNeedsSetting"]);
					return;
				}
				stageOnly = Core.ScanStage.PartialCompare;
				break;
			default:
				await MessageBoxService.Show(App.Lang["Message.CommandNotImplemented"]);
				return;
			}

			Duplicates.Clear();

			TempDirectory = TempExtractionManager.Register(new("VDF-"));
			Utils.ThumbCacheHelpers.SetActiveProvider(Utils.ThumbPack.Open(TempDirectory.Path));

			IsScanning = true;
			IsReadyToCompare = false;
			IsGathered = false;
			TotalDuplicateGroups = 0;
			TotalDuplicates = 0;
			TotalDuplicatesSize = string.Empty;

			SettingsFile.SaveSettings();
			SyncCoreSettings();

			BusyHeadingText = App.Lang["MainWindow.Busy.PleaseWait"];
			IsPauseDraining = false;
			ChangeIsBusyMessage();
			IsBusy = true;

			if (stageOnly is Core.ScanStage stage) {
				stageOnlyRunInProgress = stage is Core.ScanStage.BuildFileList or Core.ScanStage.GatherInfos;
				Scanner.StartStage(stage);
			}
			else if (isFreshScan) {
				Scanner.StartSearch();
			}
			else {
				Scanner.StartCompare();
			}
		}, CanStartScan);

		IObservable<bool> CanStartScan {
			[System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = WhenAnyValueTrimJustification)]
			get => this.WhenAnyValue(x => x.IsBusy, busy => !busy);
		}

		/// <summary>
		/// Copies the GUI settings into the Core engine. Must run before any engine
		/// operation that reads <see cref="ScanEngine.Settings"/> — not just scans:
		/// explicit thumbnail loading after a backup restore otherwise runs with Core
		/// defaults (e.g. ThumbnailMaxWidth 100 instead of the configured value),
		/// producing pixelated thumbnails (issue #778).
		/// </summary>
		void SyncCoreSettings() {
			Scanner.Settings.IncludeSubDirectories = SettingsFile.Instance.IncludeSubDirectories;
			Scanner.Settings.IncludeImages = SettingsFile.Instance.IncludeImages;
			Scanner.Settings.GeneratePreviewThumbnails = SettingsFile.Instance.GeneratePreviewThumbnails;
			Scanner.Settings.IgnoreReadOnlyFolders = SettingsFile.Instance.IgnoreReadOnlyFolders;
			Scanner.Settings.IgnoreReparsePoints = SettingsFile.Instance.IgnoreReparsePoints;
			Scanner.Settings.ExcludeHardLinks = SettingsFile.Instance.ExcludeHardLinks;
			Scanner.Settings.HardwareAccelerationMode = SettingsFile.Instance.HardwareAccelerationMode;
			Scanner.Settings.Percent = SettingsFile.Instance.Percent;
			Scanner.Settings.PercentDurationDifference = SettingsFile.Instance.PercentDurationDifference;
			Scanner.Settings.DurationDifferenceMinSeconds = SettingsFile.Instance.DurationDifferenceMinSeconds;
			Scanner.Settings.DurationDifferenceMaxSeconds = SettingsFile.Instance.DurationDifferenceMaxSeconds;
			Scanner.Settings.MaxSamplingDurationSeconds = SettingsFile.Instance.MaxSamplingDurationSeconds;
			Scanner.Settings.MaxDegreeOfParallelism = SettingsFile.Instance.MaxDegreeOfParallelism;
			Scanner.Settings.HddMaxDegreeOfParallelism = SettingsFile.Instance.HddMaxDegreeOfParallelism;
			Scanner.Settings.AdaptiveConcurrency = SettingsFile.Instance.AdaptiveConcurrency;
			Scanner.Settings.AdaptiveWindowSeconds = SettingsFile.Instance.AdaptiveWindowSeconds;
			// Snapshot of the saved per-drive caps, seeded into the drive counters at scan start so a capped
			// drive LAUNCHES at its cap (instead of starting at auto and draining down once the first progress
			// event pushes the cap). Live mid-scan changes still flow through SetDriveCap.
			Scanner.Settings.DriveWorkerCaps = new Dictionary<string, int>(SettingsFile.Instance.DriveParallelismCaps, StringComparer.OrdinalIgnoreCase);
			Scanner.Settings.ThumbnailCount = SettingsFile.Instance.Thumbnails;
			Scanner.Settings.ThumbnailMaxWidth = SettingsFile.Instance.ThumbnailMaxWidth;
			Scanner.Settings.ExtendedFFToolsLogging = SettingsFile.Instance.ExtendedFFToolsLogging;
			Scanner.Settings.LogExcludedFiles = SettingsFile.Instance.LogExcludedFiles;
			Scanner.Settings.AlwaysRetryFailedSampling = SettingsFile.Instance.AlwaysRetryFailedSampling;
			Scanner.Settings.CustomFFArguments = SettingsFile.Instance.CustomFFArguments;
			Scanner.Settings.UseNativeFfmpegBinding = SettingsFile.Instance.UseNativeFfmpegBinding;
			Scanner.Settings.IgnoreBlackPixels = SettingsFile.Instance.IgnoreBlackPixels;
			Scanner.Settings.IgnoreWhitePixels = SettingsFile.Instance.IgnoreWhitePixels;
			Scanner.Settings.CompareHorizontallyFlipped = SettingsFile.Instance.CompareHorizontallyFlipped;
			Scanner.Settings.CustomDatabaseFolder = SettingsFile.Instance.CustomDatabaseFolder;
			Scanner.Settings.DatabaseCheckpointIntervalMinutes = SettingsFile.Instance.DatabaseCheckpointIntervalMinutes;
			SettingsFile.Instance.LanguageCode = App.Lang.CurrentLanguage;
			Scanner.Settings.LanguageCode = SettingsFile.Instance.LanguageCode;
			Scanner.Settings.IncludeNonExistingFiles = SettingsFile.Instance.IncludeNonExistingFiles;
			Scanner.Settings.FilterByFilePathContains = SettingsFile.Instance.FilterByFilePathContains;
			Scanner.Settings.FilePathContainsTexts = SettingsFile.Instance.FilePathContainsTexts.ToList();
			Scanner.Settings.FilterByFilePathNotContains = SettingsFile.Instance.FilterByFilePathNotContains;
			Scanner.Settings.ScanAgainstEntireDatabase = SettingsFile.Instance.ScanAgainstEntireDatabase;
			Scanner.Settings.FolderMatchMode = SettingsFile.Instance.FolderMatchMode;
			Scanner.Settings.SameFolderDepth = SettingsFile.Instance.SameFolderDepth;
			Scanner.Settings.UsePHashing = SettingsFile.Instance.UsePHash;
			Scanner.Settings.UseExifCreationDate = SettingsFile.Instance.UseExifCreationDate;
			Scanner.Settings.FilePathNotContainsTexts = SettingsFile.Instance.FilePathNotContainsTexts.ToList();
			Scanner.Settings.FilterByFileSize = SettingsFile.Instance.FilterByFileSize;
			Scanner.Settings.MaximumFileSize = SettingsFile.Instance.MaximumFileSize;
			Scanner.Settings.MinimumFileSize = SettingsFile.Instance.MinimumFileSize;
			Scanner.Settings.EnablePartialClipDetection = SettingsFile.Instance.EnablePartialClipDetection;
			Scanner.Settings.AudioCompareMethod = SettingsFile.Instance.AudioCompareMethod;
			Scanner.Settings.PartialClipMinRatio = SettingsFile.Instance.PartialClipMinRatioPercent / 100.0;
			Scanner.Settings.PartialClipSimilarityThreshold = SettingsFile.Instance.PartialClipSimilarityThresholdPercent / 100.0;
			Scanner.Settings.PartialClipRequireVisualMatch = SettingsFile.Instance.PartialClipRequireVisualMatch;
			Scanner.Settings.PartialClipVisualThreshold = SettingsFile.Instance.PartialClipVisualThresholdPercent / 100.0;
			Scanner.Settings.ParallelAudioDecodeThreads = SettingsFile.Instance.ParallelAudioDecodeThreads;
			// Pause checkboxes must reach the engine BEFORE the scan starts (probe/LCN skip and
			// first file claims) — the live progress-tick push alone raced the scan start.
			Scanner.Settings.DriveDisabledDrives = new HashSet<string>(SettingsFile.Instance.DriveDisabledDrives, StringComparer.OrdinalIgnoreCase);
			Scanner.Settings.IncludeList.Clear();
			foreach (var s in SettingsFile.Instance.Includes)
				Scanner.Settings.IncludeList.Add(s);
			Scanner.Settings.BlackList.Clear();
			foreach (var s in SettingsFile.Instance.Blacklists)
				Scanner.Settings.BlackList.Add(s);

			// Apply the FFmpeg engine statics as well — PrepareSearch does this at scan
			// start, but thumbnail-only flows never reach PrepareSearch.
			VDF.Core.FFTools.FfmpegEngine.HardwareAccelerationMode = Scanner.Settings.HardwareAccelerationMode;
			VDF.Core.FFTools.FfmpegEngine.CustomFFArguments = Scanner.Settings.CustomFFArguments;
			VDF.Core.FFTools.FfmpegEngine.UseNativeBinding = Scanner.Settings.UseNativeFfmpegBinding;
		}

		public ReactiveCommand<Unit, Unit> PauseScanCommand => ReactiveCommand.Create(() => {
			Scanner.Pause();
			IsPaused = true;
			// In-flight files keep running to 100% — show "pausing…" until the last one finishes;
			// Scanner_Progress flips to "paused" when active rows reach zero.
			if (DriveSegments.Sum(s => s.ActiveFileLines.Count) > 0) {
				IsPauseDraining = true;
				BusyHeadingText = App.Lang["Busy.Pausing.Title"];
				IsBusyOverlayText = App.Lang["Busy.Pausing.Detail"];
			}
			else {
				IsPauseDraining = false;
				BusyHeadingText = App.Lang["Busy.Paused.Title"];
				IsBusyOverlayText = App.Lang["Busy.Paused.Detail"];
			}
		}, CanPauseScan);

		IObservable<bool> CanPauseScan {
			[System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = WhenAnyValueTrimJustification)]
			get => this.WhenAnyValue(x => x.IsScanning, x => x.IsPaused, (a, b) => a && !b);
		}

		public ReactiveCommand<Unit, Unit> ResumeScanCommand => ReactiveCommand.Create(() => {
			IsPaused = false;
			IsPauseDraining = false;
			Scanner.Resume();
			BusyHeadingText = App.Lang["MainWindow.Busy.PleaseWait"];
			ChangeIsBusyMessage();
		}, CanResumeScan);

		IObservable<bool> CanResumeScan {
			[System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = WhenAnyValueTrimJustification)]
			get => this.WhenAnyValue(x => x.IsScanning, x => x.IsPaused, (a, b) => a && b);
		}

		public ReactiveCommand<Unit, Unit> StopScanCommand => ReactiveCommand.Create(() => {
			IsPaused = false;
			IsPauseDraining = false;
			// Safe stop (gather phase, first press): the UI stays live so the per-drive rows can be
			// watched draining as each in-flight file finishes to 100%; pressing Stop again force-aborts.
			// Only a hard cancellation gets the blocking "stopping..." overlay (it resolves quickly).
			bool draining = Scanner.Stop();
			if (draining) {
				BusyHeadingText = App.Lang["Busy.Stopping.Title"];
				IsBusyOverlayText = App.Lang["Busy.Stopping.Detail"];
			}
			else {
				IsBusy = true;
				BusyHeadingText = App.Lang["MainWindow.Busy.PleaseWait"];
				IsBusyOverlayText = "Stopping all scan threads...";
			}
		}, CanStopScan);

		IObservable<bool> CanStopScan {
			[System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = WhenAnyValueTrimJustification)]
			get => this.WhenAnyValue(x => x.IsScanning);
		}

		public ReactiveCommand<Unit, Unit> MarkGroupAsNotAMatchCommand => ReactiveCommand.CreateFromTask(async () => {
			try {
				if (GetSelectedDuplicateItem() is not DuplicateItemVM data) return;

				var gid = data.ItemInfo.GroupId;

				HashSet<string> blacklist = new HashSet<string>(PathComparer.ForCurrentPlatform);
				foreach (DuplicateItemVM duplicateItem in Duplicates.Where(d => d.ItemInfo.GroupId == gid)) {
					blacklist.Add(duplicateItem.ItemInfo.Path);
					// Also store the content oshash so this mark survives a later move/rename.
					string? oshash = ScanEngine.GetOsHash(duplicateItem.ItemInfo.Path);
					if (!string.IsNullOrEmpty(oshash))
						blacklist.Add(GroupBlacklistFilter.OsHashToken(oshash));
				}
				GroupBlacklist.Add(blacklist);
				try {
					await BlacklistStore.SaveAsync(BlacklistedGroupsFile, GroupBlacklist);
				}
				catch (Exception e) {
					GroupBlacklist.Remove(blacklist);
					await MessageBoxService.Show(e.Message);
					return;
				}

				// Remove all items in this group from the list
				for (int i = Duplicates.Count - 1; i >= 0; i--)
					if (Duplicates[i].ItemInfo.GroupId == gid)
						Duplicates.RemoveAt(i);

				// Drop singleton groups
				DropSingletonGroups();
				RefreshGroupStats();
				view?.Refresh();

				// Mirror the deletion path: keep backup.scanresults in sync so the mark
				// survives a crash before the user gets to a clean exit.
				if (SettingsFile.Instance.BackupAfterListChanged)
					await ExportScanResults(BackupScanResultsFile);
			}
			catch (Exception ex) {
				Logger.Instance.Info($"MarkGroupAsNotAMatch failed: {ex}");
			}
		});

		private HashSet<Guid> ComputeBlacklistedGroupIds(IEnumerable<(Guid GroupId, string Path, string? OsHash)> items) =>
			GroupBlacklistFilter.ComputeBlacklistedGroupIds(items, GroupBlacklist);

		public ReactiveCommand<Unit, Unit> OpenBlacklistManagerCommand => ReactiveCommand.Create(() => {
			var vm = new BlacklistManagerVM(GroupBlacklist,
				() => BlacklistStore.SaveAsync(BlacklistedGroupsFile, GroupBlacklist));
			new BlacklistManagerView(vm).Show();
		});

		public ReactiveCommand<Unit, Unit> ShowGroupInThumbnailComparerCommand => ReactiveCommand.Create(() => {
			if (GetSelectedDuplicateItem() is not DuplicateItemVM data) return;
			List<LargeThumbnailDuplicateItem> items = new();
			Guid? groupId = null;

			if (GetSelectedDuplicates().Count == 1) {
				foreach (DuplicateItemVM duplicateItem in Duplicates.Where(d => d.ItemInfo.GroupId == data.ItemInfo.GroupId))
					items.Add(new LargeThumbnailDuplicateItem(duplicateItem));
				groupId = data.ItemInfo.GroupId;
			}
			else {
				foreach (DuplicateItemVM duplicateItem in GetSelectedDuplicates())
					items.Add(new LargeThumbnailDuplicateItem(duplicateItem));
			}

			ThumbnailComparer thumbnailComparer = new(items, groupId, NavigateGroupForComparer);
			thumbnailComparer.Show();
		});

		public void CompareGroup(Guid groupId) {
			var items = Duplicates
				.Where(d => d.ItemInfo.GroupId == groupId)
				.Select(d => new LargeThumbnailDuplicateItem(d))
				.ToList();
			if (items.Count == 0) return;
			new ThumbnailComparer(items, groupId, NavigateGroupForComparer).Show();
		}

		public void KeepBestInGroup(Guid groupId) {
			var groupItems = Duplicates.Where(d => d.ItemInfo.GroupId == groupId).ToList();
			if (groupItems.Count < 2) return;

			var keep = VDF.Core.Utils.QualityRanker.PickKeeper(
				groupItems,
				ResolveCriteria(QualityCriteriaOrder),
				d => d.ItemInfo.IsImage);

			using var _ = BeginSelectionUndoBatch();
			keep.Checked = false;
			foreach (var item in groupItems)
				if (item.ItemInfo.Path != keep.ItemInfo.Path)
					item.Checked = true;
		}

	public ReactiveCommand<Unit, Unit> LoadThumbnailsForCheckedItemsCommand => ReactiveCommand.CreateFromTask(async () => {
		var items = Duplicates.Where(d => d.Checked).Select(vm => vm.ItemInfo).ToList();
		if (items.Count == 0) return;
		SyncCoreSettings();
		await Scanner.RetrieveThumbnailsForItems(items);
	});

	public ReactiveCommand<Unit, Unit> LoadThumbnailsForGroupCommand => ReactiveCommand.CreateFromTask(async () => {
		if (GetSelectedDuplicateItem() is not DuplicateItemVM currentItem) return;
		var items = Duplicates.Where(d => d.ItemInfo.GroupId == currentItem.ItemInfo.GroupId).Select(d => d.ItemInfo).ToList();
		SyncCoreSettings();
		await Scanner.RetrieveThumbnailsForItems(items);
	});

		List<DuplicateItemVM> CheckedItemsToDelete => ScopedDuplicates().Where(d => d.Checked && d.IsVisibleInFilter).ToList();

		async void DeleteInternal(bool fromDisk,
									List<DuplicateItemVM>? toDelete = null,
									bool blackList = false,
									bool createSymbolLinksInstead = false,
									bool permanently = false,
									bool createHardLinksInstead = false) {
			if (Duplicates.Count == 0) return;
			toDelete ??= CheckedItemsToDelete;
			if (toDelete.Count == 0) return;
			bool createLinks = createSymbolLinksInstead || createHardLinksInstead;

			long totalSizeToDelete = 0;
			foreach (var item in toDelete)
				totalSizeToDelete += CheckedSizeOf(item);

			string confirmMessage = createLinks
					? App.Lang["Message.ReplaceWithLinksConfirm"]
					: fromDisk
					? (!permanently ? App.Lang["Message.DeleteToTrashConfirm"] : App.Lang["Message.DeletePermanentlyConfirm"])
					: (blackList ? App.Lang["Message.DeleteFromListBlacklistConfirm"] : App.Lang["Message.DeleteFromListConfirm"]);
			confirmMessage += Environment.NewLine + Environment.NewLine +
				string.Format(App.Lang["Message.DeleteConfirmStats"], toDelete.Count, totalSizeToDelete.BytesToString());

			MessageBoxButtons? dlgResult = await MessageBoxService.Show(confirmMessage,
				MessageBoxButtons.Yes | MessageBoxButtons.No);
			if (dlgResult != MessageBoxButtons.Yes) return;

			var keepByGroup = Duplicates
				   .GroupBy(d => d.ItemInfo.GroupId)
				   .ToDictionary(
					   g => g.Key,
					   // Prefer an unchecked item whose file still exists — a stale unchecked entry
					   // (moved/deleted externally since the scan) would otherwise be picked as the
					   // "keeper" even though a different unchecked member is the real survivor.
					   g => g.FirstOrDefault(x => !x.Checked && File.Exists(x.ItemInfo.Path)) ?? g.FirstOrDefault(x => !x.Checked)
				   );

			var actuallyDeleted = new HashSet<DuplicateItemVM>(toDelete.Count, ReferenceEqualityComparer<DuplicateItemVM>.Instance);
			// Subset of actuallyDeleted that reflects a REAL disk change this pass (recycled/deleted/linked
			// content, or content destroyed even if a subsequent link creation then failed) — used only to
			// gate the survivor archive below. actuallyDeleted itself also includes items whose file was
			// already gone before this operation ("removing entry only"), which must not trigger archiving.
			var diskContentChanged = new HashSet<DuplicateItemVM>(toDelete.Count, ReferenceEqualityComparer<DuplicateItemVM>.Instance);
			// Whole-group checked-delete (no unchecked survivor) is a content rejection: keep exactly
			// one fingerprint as a tombstone. This set records the groups that already kept theirs.
			var tombstonedGroups = new HashSet<Guid>();
			long freedBytes = 0;
			int total = toDelete.Count;
			IsBusy = true;
			IsBusyOverlayText = string.Format(App.Lang["Busy.Deleting"], 0, total);

			try {
				// File I/O and database updates run off the UI thread; thousands of
				// deletions previously froze the window for their full duration.
				await Task.Run(() => {
					var progressTimer = Stopwatch.StartNew();
					int done = 0;
					void ReportProgress() {
						if (progressTimer.ElapsedMilliseconds < 300) return;
						progressTimer.Restart();
						int current = done;
						Dispatcher.UIThread.Post(() =>
							IsBusyOverlayText = string.Format(App.Lang["Busy.Deleting"], current, total));
					}

					// Windows recycle-bin deletes go through a single batched shell
					// operation — one SHFileOperation per file pays the full shell
					// round-trip each time and is dramatically slower for big batches.
					// Per-file success is determined afterwards by re-checking existence.
					bool batchedRecycle = fromDisk && !permanently && !createLinks && CoreUtils.IsWindows;
					var batchRecycled = new HashSet<DuplicateItemVM>(ReferenceEqualityComparer<DuplicateItemVM>.Instance);
					if (batchedRecycle) {
						var existing = toDelete.Where(d => File.Exists(d.ItemInfo.Path)).ToList();
						if (existing.Count > 0) {
							var fs = new FileUtils.SHFILEOPSTRUCT {
								wFunc = FileUtils.FileOperationType.FO_DELETE,
								pFrom = string.Join('\0', existing.Select(d => d.ItemInfo.Path)) + "\0\0",
								fFlags = FileUtils.FileOperationFlags.FOF_ALLOWUNDO |
										 FileUtils.FileOperationFlags.FOF_NOCONFIRMATION |
										 FileUtils.FileOperationFlags.FOF_NOERRORUI |
										 FileUtils.FileOperationFlags.FOF_SILENT
							};
							int result = FileUtils.SHFileOperation(ref fs);
							if (result != 0)
								Logger.Instance.Info($"SHFileOperation returned {result:X} for a batch of {existing.Count} file(s); checking which files were actually recycled.");
							foreach (var d in existing)
								batchRecycled.Add(d);
						}
					}

					foreach (var dub in toDelete) {
						try {
							// Path-only entry for the database lookup; FileEntry(string)
							// stats the file and throws once it's gone.
							var fe = new FileEntry { Path = dub.ItemInfo.Path };
							bool exists = File.Exists(dub.ItemInfo.Path);

							if (createLinks) {
								if (!exists) {
									Logger.Instance.Info($"'{dub.ItemInfo.Path}' no longer exists on disk; removing entry only.");
								}
								else {
									var keeper = keepByGroup.TryGetValue(dub.ItemInfo.GroupId, out var k) ? k : null;
									if (keeper == null)
										throw new Exception($"Cannot create a link for '{dub.ItemInfo.Path}' because all items in this group are selected");
									if (!File.Exists(keeper.ItemInfo.Path))
										throw new Exception($"Cannot create a link for '{dub.ItemInfo.Path}' because the file to keep ('{keeper.ItemInfo.Path}') does not exist");
									// The link target path must be free before the link can be created. Content is
									// gone the instant this succeeds — record it even if the link call below then
									// throws, so a group whose dup was destroyed but not linked still gets archived.
									File.Delete(dub.ItemInfo.Path);
									diskContentChanged.Add(dub);
									if (createHardLinksInstead)
										HardLinkUtils.CreateHardLink(dub.ItemInfo.Path, keeper.ItemInfo.Path);
									else
										File.CreateSymbolicLink(dub.ItemInfo.Path, keeper.ItemInfo.Path);
									freedBytes += CheckedSizeOf(dub);
								}
							}
							else if (fromDisk) {
								if (!exists) {
									if (batchRecycled.Contains(dub)) {
										freedBytes += CheckedSizeOf(dub);
										diskContentChanged.Add(dub);
									}
									else {
										// File was already gone before this operation — treat as successfully
										// deleted so the entry is still removed from the list and database,
										// but this pass didn't touch disk content (no diskContentChanged mark).
										Logger.Instance.Info($"'{dub.ItemInfo.Path}' no longer exists on disk; removing entry only.");
									}
								}
								else if (batchedRecycle) {
									// Batch ran but this file is still there.
									throw new Exception("the shell did not move the file to the recycle bin");
								}
								else if (!permanently) {
									// Linux/macOS: attempt to move to system trash, fall back to permanent delete
									if (!FileUtils.MoveToTrash(dub.ItemInfo.Path))
										File.Delete(dub.ItemInfo.Path);
									freedBytes += CheckedSizeOf(dub);
									diskContentChanged.Add(dub);
								}
								else {
									File.Delete(dub.ItemInfo.Path);
									freedBytes += CheckedSizeOf(dub);
									diskContentChanged.Add(dub);
								}
							}

							if (blackList)
								ScanEngine.BlackListFileEntry(dub.ItemInfo.Path);
							else {
								// A checked-delete that removes an ENTIRE group (no unchecked survivor) is a content
								// rejection: keep exactly one fingerprint as a tombstone so a re-download is caught. A
								// partial delete leaves a live survivor, so every deleted entry is dropped. TOMBSTONE-DESIGN.md.
								bool wholeGroupDeleted = !keepByGroup.TryGetValue(dub.ItemInfo.GroupId, out var survivor) || survivor == null;
								if (!(wholeGroupDeleted && tombstonedGroups.Add(dub.ItemInfo.GroupId)))
									ScanEngine.RemoveFromDatabase(fe);
							}

							actuallyDeleted.Add(dub);
						}
						catch (Exception ex) {
							Logger.Instance.Info($"Failed to delete '{dub.ItemInfo.Path}': {ex.Message}\n{ex.StackTrace}");
						}
						finally {
							done++;
							ReportProgress();
						}
					}
				});
			}
			finally {
				IsBusy = false;
			}

			// Each group that just lost members leaves its unique survivor a permanent visual record:
			// one frame in the append-only SurvivorThumbs.sqlite (content-keyed; see SurvivorThumbArchive).
			// Disk-affecting dedupe only — gated on diskContentChanged (not actuallyDeleted, which also
			// includes "file was already gone" list-only cleanups that never touched content) — so a
			// stale-list prune with zero real deletions never triggers an archive write.
			if ((fromDisk || createLinks) && diskContentChanged.Count > 0) {
				var survivorList = new List<DuplicateItemVM>();
				var seenGroups = new HashSet<Guid>();
				foreach (var d in diskContentChanged)
					if (seenGroups.Add(d.ItemInfo.GroupId) &&
						keepByGroup.TryGetValue(d.ItemInfo.GroupId, out var survivor) && survivor != null)
						survivorList.Add(survivor);
				if (survivorList.Count > 0)
					_ = Task.Run(() => {
						try {
							int n = Utils.SurvivorThumbArchive.Archive(survivorList);
							Logger.Instance.Info($"Survivor thumbnail archive: {n} new frame(s) stored.");
						}
						catch (Exception ex) {
							Logger.Instance.Info($"Survivor thumbnail archive failed: {ex.Message}");
						}
					});
			}

			if (freedBytes > 0)
				TotalSizeRemovedInternal += freedBytes;

			int failedCount = toDelete.Count - actuallyDeleted.Count;
			if (failedCount > 0)
				await MessageBoxService.Show(string.Format(App.Lang["Message.DeleteCompletedWithFailures"],
					actuallyDeleted.Count, toDelete.Count, failedCount));

			if (actuallyDeleted.Count == 0)
				return;

			// Remove deleted items from flat list
			foreach (var item in actuallyDeleted) {
				for (int i = Duplicates.Count - 1; i >= 0; i--)
					if (ReferenceEquals(Duplicates[i], item)) { Duplicates.RemoveAt(i); break; }
			}

			// When ExcludeHardLinks is enabled, remove items within each group
			// that are hardlinks of another remaining item in the same group.
			if (SettingsFile.Instance.ExcludeHardLinks)
				DropHardLinkDuplicates();

			// Drop groups that have only one item left (no longer duplicates)
			DropSingletonGroups();

			RefreshGroupStats();
			view?.Refresh();

			ScanEngine.SaveDatabase();

			if (SettingsFile.Instance.BackupAfterListChanged)
				await ExportScanResults(BackupScanResultsFile);
		}

		private void DropSingletonGroups() {
			var singletonGroups = Duplicates
				.GroupBy(d => d.ItemInfo.GroupId)
				.Where(g => g.Count() <= 1)
				.Select(g => g.Key)
				.ToHashSet();

			if (singletonGroups.Count == 0) return;

			for (int i = Duplicates.Count - 1; i >= 0; i--)
				if (singletonGroups.Contains(Duplicates[i].ItemInfo.GroupId))
					Duplicates.RemoveAt(i);
		}

		/// <summary>
		/// After deletion, re-check remaining groups for items that are hardlinks
		/// of each other. Within each group, keep only one representative per set
		/// of hardlinked files.
		/// </summary>
		private void DropHardLinkDuplicates() {
			var toRemove = new List<DuplicateItemVM>();
			foreach (var group in Duplicates.GroupBy(d => d.ItemInfo.GroupId)) {
				var items = group.ToList();
				if (items.Count <= 1) continue;
				var kept = new List<DuplicateItemVM>();
				foreach (var item in items) {
					bool isHardLinkOfKept = false;
					foreach (var k in kept) {
						if (item.ItemInfo.SizeLong == k.ItemInfo.SizeLong &&
							HardLinkUtils.AreSameFile(item.ItemInfo.Path, k.ItemInfo.Path)) {
							isHardLinkOfKept = true;
							break;
						}
					}
					if (isHardLinkOfKept)
						toRemove.Add(item);
					else
						kept.Add(item);
				}
			}

			foreach (var item in toRemove)
				for (int i = Duplicates.Count - 1; i >= 0; i--)
					if (ReferenceEquals(Duplicates[i], item)) { Duplicates.RemoveAt(i); break; }
		}

		public ReactiveCommand<Unit, Unit> ExpandAllGroupsCommand => ReactiveCommand.Create(() => {
			if (view == null) return;
			foreach (var group in view.Groups ?? Enumerable.Empty<object>())
				if (group is DataGridCollectionViewGroup g)
					GetDataGrid.ExpandRowGroup(g, true);
		});

		public ReactiveCommand<Unit, Unit> CollapseAllGroupsCommand => ReactiveCommand.Create(() => {
			if (view == null) return;
			foreach (var group in view.Groups ?? Enumerable.Empty<object>())
				if (group is DataGridCollectionViewGroup g)
					GetDataGrid.CollapseRowGroup(g, true);
		});

		public ReactiveCommand<Unit, Unit> NavigateNextGroupCommand => ReactiveCommand.Create(() => {
			NavigateGroup(forward: true);
		});

		// Keyboard triage (Alt+K): keep the highlighted item — check everything
		// else in its group — then advance to the next group.
		public ReactiveCommand<Unit, Unit> KeepHighlightedAndAdvanceCommand => ReactiveCommand.Create(() => {
			if (GetSelectedDuplicateItem() is DuplicateItemVM keeper) {
				using var _ = BeginSelectionUndoBatch();
				keeper.Checked = false;
				foreach (var item in Duplicates)
					if (item.ItemInfo.GroupId == keeper.ItemInfo.GroupId && !ReferenceEquals(item, keeper))
						item.Checked = true;
			}
			NavigateGroup(forward: true);
		});

		public ReactiveCommand<Unit, Unit> NavigatePreviousGroupCommand => ReactiveCommand.Create(() => {
			NavigateGroup(forward: false);
		});

		Guid? NavigateGroup(bool forward, Guid? fromGroupId = null) {
			if (view?.Groups == null) return null;
			var groups = view.Groups.OfType<DataGridCollectionViewGroup>().ToList();
			if (groups.Count == 0) return null;

			var dataGrid = GetDataGrid;
			Guid? referenceGroupId = fromGroupId
				?? (dataGrid.SelectedItem as DuplicateItemVM)?.ItemInfo.GroupId;
			int currentGroupIndex = -1;

			if (referenceGroupId.HasValue) {
				for (int i = 0; i < groups.Count; i++) {
					if (groups[i].Items.OfType<DuplicateItemVM>()
						.Any(item => item.ItemInfo.GroupId == referenceGroupId.Value)) {
						currentGroupIndex = i;
						break;
					}
				}
			}

			int targetIndex = forward
				? (currentGroupIndex + 1 < groups.Count ? currentGroupIndex + 1 : 0)
				: (currentGroupIndex - 1 >= 0 ? currentGroupIndex - 1 : groups.Count - 1);

			var targetGroup = groups[targetIndex];
			dataGrid.ExpandRowGroup(targetGroup, true);
			var firstItem = targetGroup.Items.OfType<DuplicateItemVM>().FirstOrDefault();
			if (firstItem != null) {
				dataGrid.SelectedItem = firstItem;
				dataGrid.ScrollIntoView(firstItem, null);
				return firstItem.ItemInfo.GroupId;
			}
			return null;
		}

		// Used by ThumbnailComparer to walk to the sibling group without closing the dialog.
		// Also moves the main grid's selection so state stays consistent when the dialog closes.
		internal (Guid GroupId, List<LargeThumbnailDuplicateItem> Items)? NavigateGroupForComparer(Guid currentGroupId, bool forward) {
			var newGroupId = NavigateGroup(forward, currentGroupId);
			if (newGroupId is null) return null;
			var items = Duplicates
				.Where(d => d.ItemInfo.GroupId == newGroupId.Value)
				.Select(d => new LargeThumbnailDuplicateItem(d))
				.ToList();
			if (items.Count == 0) return null;
			return (newGroupId.Value, items);
		}

		public ReactiveCommand<Unit, Unit> CopyPathsToClipboardCommand => ReactiveCommand.CreateFromTask(async () => {
			StringBuilder sb = new();
			foreach (var item in GetSelectedDuplicates()) {
				if (item is not DuplicateItemVM currentItem) return;
				sb.AppendLine($"\"{currentItem.ItemInfo.Path}\"");
			}
			if (ApplicationHelpers.MainWindow.Clipboard is { } clipboard)
				await clipboard.SetTextAsync(sb.ToString().TrimEnd(new char[2] { '\r', '\n' }));
		});

		public ReactiveCommand<Unit, Unit> CopyFilenamesToClipboardCommand => ReactiveCommand.CreateFromTask(async () => {
			StringBuilder sb = new();
			foreach (var item in GetSelectedDuplicates()) {
				if (item is not DuplicateItemVM currentItem) return;
				sb.AppendLine(Path.GetFileName(currentItem.ItemInfo.Path));
			}
			if (ApplicationHelpers.MainWindow.Clipboard is { } clipboard)
				await clipboard.SetTextAsync(sb.ToString().TrimEnd(new char[2] { '\r', '\n' }));
		});

		public ReactiveCommand<Unit, Unit> RelocateDatabaseFilesCommand => ReactiveCommand.Create(() => {
			var dlg = new RelocateFilesDialog();
			dlg.ShowDialog(ApplicationHelpers.MainWindow);
		});
	}
}
