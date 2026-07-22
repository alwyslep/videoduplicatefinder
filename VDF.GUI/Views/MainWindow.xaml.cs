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
using System.Windows.Input;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using VDF.Core.Utils;
using VDF.GUI.Data;
using VDF.GUI.Mvvm;
using VDF.GUI.ViewModels;

namespace VDF.GUI.Views {
	public class MainWindow : Window {
		bool keepBackupFile;
		bool hasExited;

		public readonly Core.FFTools.FFHardwareAccelerationMode InitialHwMode;
		public MainWindow() {
			//Settings must be load before XAML is parsed
			SettingsFile.LoadSettings();
			App.Lang.CurrentLanguage = SettingsFile.Instance.LanguageCode;

			InitializeComponent();
			Closing += MainWindow_Closing;
			Opened += MainWindow_Opened;
			//Don't use this Window.OnClosing event,
			//datacontext might not be the same due to Avalonia internal handling data differently



			this.FindControl<ListBox>("ListboxBlacklist")!.AddHandler(DragDrop.DropEvent, DropBlacklist);
			this.FindControl<ListBox>("ListboxBlacklist")!.AddHandler(DragDrop.DragOverEvent, DragOver);
			// Drag-to-exclude: drag a folder out of the directory tree onto the exclude list.
			// handledEventsToo because TreeViewItem marks presses handled while selecting.
			var directoryTree = this.FindControl<TreeView>("DirectoryTree")!;
			directoryTree.AddHandler(InputElement.PointerPressedEvent, DirectoryTree_PointerPressed, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
			directoryTree.AddHandler(InputElement.PointerMovedEvent, DirectoryTree_PointerMoved, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
			directoryTree.AddHandler(InputElement.PointerReleasedEvent, DirectoryTree_PointerReleased, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);

			ApplicationHelpers.CurrentApplicationLifetime.Startup += MainWindow_Startup;
			ApplicationHelpers.CurrentApplicationLifetime.Exit += MainWindow_Exit;
			ApplicationHelpers.CurrentApplicationLifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;

			if (SettingsFile.Instance.UseMica &&
				RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
				Environment.OSVersion.Version.Build >= 22000) {
				Background = null;
				TransparencyLevelHint = new List<WindowTransparencyLevel> { WindowTransparencyLevel.Mica };
				// Avalonia 12: ExtendClientAreaChromeHints was removed; WindowDecorations.Full
				// (system chrome) is the default, matching the old PreferSystemChrome behavior.
				if (SettingsFile.Instance.DarkMode)
					this.FindControl<ExperimentalAcrylicBorder>("ExperimentalAcrylicBorderBackgroundBlack")!.IsVisible = true;
				else
					this.FindControl<ExperimentalAcrylicBorder>("ExperimentalAcrylicBorderBackgroundWhite")!.IsVisible = true;
			}

			// GNOME (and other Linux compositors) keep their server-side title bar even when
			// ExtendClientAreaToDecorationsHint is set, so VDF's own centered title rendered a
			// second time just below the decoration (#798). Fall back to native decorations on
			// Linux and drop both the in-window title and the gap reserved for the extended
			// caption area. Windows/macOS keep the custom chrome.
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
				ExtendClientAreaToDecorationsHint = false;
				this.FindControl<TextBlock>("TextBlockWindowTitle")!.IsVisible = false;
				this.FindControl<Grid>("MainContentGrid")!.Margin = new Thickness(2, 2, 2, 2);
			}

			if (!SettingsFile.Instance.DarkMode)
				RequestedThemeVariant = ThemeVariant.Light;

			// Switch theme at runtime when the user toggles the DarkMode setting
			SettingsFile.Instance.PropertyChanged += (_, e) => {
				if (e.PropertyName == nameof(SettingsFile.DarkMode))
					RequestedThemeVariant = SettingsFile.Instance.DarkMode ? ThemeVariant.Dark : ThemeVariant.Light;
			};

			ShowAlgoView();
		}

		async void ShowAlgoView() {

			if (File.Exists(FileUtils.SafePathCombine(
					CoreUtils.ResolveDatabaseFolder(SettingsFile.Instance.CustomDatabaseFolder),
					"ScannedFiles.db")))
					return;

			while (!this.IsVisible) {
				await Task.Delay(200);
			}
			var dlg = new Views.ChooseAlgoView();
			await dlg.ShowDialog(this);
			if (dlg.FindControl<RadioButton>("Cb16x16")?.IsChecked == true)
				VDF.Core.Utils.DatabaseUtils.Create16x16Database();
		}

		private void MainWindow_Opened(object? sender, EventArgs e) {
			// Constrain maximize to the monitor work area (don't cover the taskbar). Register
			// before ApplySavedWindowPlacement so an app that opens maximized is constrained too.
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
				HookMaximizeToWorkArea();
			if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) {
				/*
				 * Due to Avalonia bug, window is bigger than screen size.
				 * Status bar is hidden by MacOS launch bar,
				 * see https://github.com/0x90d/videoduplicatefinder/issues/391
				 */
				Height = 750d;
			}

			ApplySavedWindowPlacement();

			ApplyKeyboardShortcuts();
			KeyboardShortcutManager.Instance.ShortcutsChanged += ApplyKeyboardShortcuts;

			HideOwnTitleIfChromeDrawsOne();
		}

		/// <summary>
		/// Avalonia draws managed window decorations — including a centered title —
		/// whenever the client area is extended (always on Linux; on Windows since
		/// Avalonia 12 removed PreferSystemChrome). The app's own title TextBlock then
		/// duplicates it. Detect the drawn title instead of hardcoding platforms so
		/// each OS keeps exactly one title. The decorations attach asynchronously
		/// (after Opened — a single deferred check missed them), so probe on the
		/// first layout passes and stop once found or after a few attempts.
		/// </summary>
		void HideOwnTitleIfChromeDrawsOne() {
			int attempts = 0;
			void Check(object? sender, EventArgs e) {
				bool chromeTitleVisible = this.GetVisualDescendants()
					.Any(c => c is Control { Name: "PART_TitleTextPanel", IsVisible: true });
				if (chromeTitleVisible) {
					this.FindControl<TextBlock>("TextBlockWindowTitle")!.IsVisible = false;
					LayoutUpdated -= Check;
				}
				else if (++attempts >= 30) {
					LayoutUpdated -= Check; // no managed chrome on this platform/config
				}
			}
			LayoutUpdated += Check;
			Check(null, EventArgs.Empty);
		}

		void ApplySavedWindowPlacement() {
			var settings = SettingsFile.Instance;
			if (settings.MainWindowWidth is double savedWidth && savedWidth > 0)
				Width = savedWidth;
			if (settings.MainWindowHeight is double savedHeight && savedHeight > 0)
				Height = savedHeight;

			if (settings.MainWindowPositionX.HasValue && settings.MainWindowPositionY.HasValue && Screens != null) {
				// Only restore a position that is still on a connected screen — a saved
				// position on a since-removed monitor would open the window off-screen.
				var saved = new PixelPoint(settings.MainWindowPositionX.Value, settings.MainWindowPositionY.Value);
				var screen = Screens.ScreenFromPoint(new PixelPoint(
					saved.X + (int)Math.Round(Width / 2), saved.Y + (int)Math.Round(Height / 2)));
				if (screen != null) {
					var workingArea = screen.WorkingArea;
					int maxX = Math.Max(workingArea.X, workingArea.Right - (int)Math.Ceiling(Width));
					int maxY = Math.Max(workingArea.Y, workingArea.Bottom - (int)Math.Ceiling(Height));
					Position = new PixelPoint(
						Math.Clamp(saved.X, workingArea.X, maxX),
						Math.Clamp(saved.Y, workingArea.Y, maxY));
				}
			}

			if (settings.MainWindowMaximized)
				WindowState = WindowState.Maximized;
		}

		void SaveWindowPlacement() {
			var settings = SettingsFile.Instance;
			settings.MainWindowMaximized = WindowState == WindowState.Maximized;
			// Size/position are only meaningful in the normal state; keep the last
			// normal-state values when closing maximized or minimized.
			if (WindowState == WindowState.Normal) {
				settings.MainWindowWidth = Width;
				settings.MainWindowHeight = Height;
				settings.MainWindowPositionX = Position.X;
				settings.MainWindowPositionY = Position.Y;
			}
		}

		void ApplyKeyboardShortcuts() {
			var vm = ApplicationHelpers.MainWindowDataContext;
			var commandMap = new Dictionary<string, ICommand> {
				["RenameFile"] = vm.RenameFileCommand,
				["ToggleCheckbox"] = vm.ToggleCheckboxCommand,
				["OpenItemsByColId"] = vm.OpenItemsByColIdCommand,
				["OpenItemInFolder"] = vm.OpenItemInFolderCommand,
				["DeleteCheckedItemsWithPrompt"] = vm.DeleteCheckedItemsWithPromptCommand,
				["DeleteCheckedItems"] = vm.DeleteCheckedItemsCommand,
				["DeleteHighlighted"] = vm.DeleteHighlightedCommand,
				["ShowGroupInThumbnailComparer"] = vm.ShowGroupInThumbnailComparerCommand,
				["MarkGroupAsNotAMatch"] = vm.MarkGroupAsNotAMatchCommand,
				["CopyCheckedItems"] = vm.CopyCheckedItemsCommand,
				["MoveCheckedItems"] = vm.MoveCheckedItemsCommand,
				["CheckLowestQuality"] = vm.CheckLowestQualityCommand,
				["ClearCheckedItems"] = vm.ClearCheckedItemsCommand,
				["InvertCheckedItems"] = vm.InvertCheckedItemsCommand,
				["ExpandAllGroups"] = vm.ExpandAllGroupsCommand,
				["CollapseAllGroups"] = vm.CollapseAllGroupsCommand,
				["RemoveCheckedItemsFromList"] = vm.RemoveCheckedItemsFromListCommand,
				["NavigateNextGroup"] = vm.NavigateNextGroupCommand,
				["NavigatePreviousGroup"] = vm.NavigatePreviousGroupCommand,
				["KeepHighlightedAndAdvance"] = vm.KeepHighlightedAndAdvanceCommand,
				["UndoSelection"] = vm.UndoSelectionCommand,
			};
			var dataGrid = this.FindControl<DataGrid>("dataGridGrouping")!;
			KeyboardShortcutManager.Instance.ApplyBindings(dataGrid, commandMap);
		}

		void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e) {
			SaveWindowPlacement();
			e.Cancel = true;
			ConfirmClose();
		}

		async void ConfirmClose() {
			try {
				if (!keepBackupFile)
					File.Delete(ApplicationHelpers.MainWindowDataContext.BackupScanResultsFile);
			}
			catch { }
			if (keepBackupFile = await ApplicationHelpers.MainWindowDataContext.SaveScanResults()) {
				Closing -= MainWindow_Closing;
				ApplicationHelpers.CurrentApplicationLifetime.Shutdown();
			}
		}

		void MainWindow_Exit(object? sender, ControlledApplicationLifetimeExitEventArgs e) {
			if (hasExited) return;
			hasExited = true;
			SettingsFile.SaveSettings();
		}

		private void DragOver(object? sender, DragEventArgs e) {
			// Only allow Copy or Link as Drop Operations.
			e.DragEffects &= (DragDropEffects.Copy | DragDropEffects.Link);

			// Only allow if the dragged data contains filenames (Explorer) or a folder path as
			// text (directory-tree drag-to-exclude).
			if (!e.DataTransfer.Contains(DataFormat.File) && !e.DataTransfer.Contains(DataFormat.Text))
				e.DragEffects = DragDropEffects.None;
		}

		private void DropBlacklist(object? sender, DragEventArgs e) {
			if (e.DataTransfer.Contains(DataFormat.File)) {
				foreach (var path in e.DataTransfer.GetItems(DataFormat.File) ?? Array.Empty<IDataTransferItem>()) {
					IStorageItem? fold = path.TryGetFile();
					if (fold == null)
						continue;
					string? localPath = fold.TryGetLocalPath();
					if (!string.IsNullOrEmpty(localPath) && !SettingsFile.Instance.Blacklists.Contains(localPath))
						SettingsFile.Instance.Blacklists.Add(localPath);
				}
				return;
			}
			// Directory-tree drag carries the folder path as plain text.
			if (e.DataTransfer.TryGetText() is { Length: > 0 } text && Directory.Exists(text) &&
				!SettingsFile.Instance.Blacklists.Contains(text))
				SettingsFile.Instance.Blacklists.Add(text);
		}

		// ── directory-tree drag source ───────────────────────────────────────────────────────────
		PointerPressedEventArgs? treeDragPress;
		DirectoryTreeNodeVM? treeDragNode;
		Point treeDragOrigin;

		void DirectoryTree_PointerPressed(object? sender, PointerPressedEventArgs e) {
			treeDragPress = null;
			treeDragNode = null;
			if (!e.GetCurrentPoint(sender as Visual).Properties.IsLeftButtonPressed) return;
			// Presses on interactive parts (tick box, expander arrow) keep their click behaviour.
			for (var v = e.Source as Visual; v != null && v != sender; v = v.GetVisualParent())
				if (v is Avalonia.Controls.Primitives.ToggleButton) return;
			if ((e.Source as Control)?.DataContext is not DirectoryTreeNodeVM node || string.IsNullOrEmpty(node.Path)) return;
			treeDragPress = e;
			treeDragNode = node;
			treeDragOrigin = e.GetPosition(this);
		}

		async void DirectoryTree_PointerMoved(object? sender, PointerEventArgs e) {
			if (treeDragPress == null || treeDragNode == null) return;
			// Disarm if the button was released outside our sight (capture lost, window deactivated).
			if (!e.GetCurrentPoint(sender as Visual).Properties.IsLeftButtonPressed) {
				treeDragPress = null;
				treeDragNode = null;
				return;
			}
			Point pos = e.GetPosition(this);
			if (Math.Abs(pos.X - treeDragOrigin.X) < 4 && Math.Abs(pos.Y - treeDragOrigin.Y) < 4) return;
			var press = treeDragPress;
			string path = treeDragNode.Path;
			treeDragPress = null;
			treeDragNode = null;
			var data = new DataTransfer();
			data.Add(DataTransferItem.CreateText(path));
			await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Copy);
		}

		void DirectoryTree_PointerReleased(object? sender, PointerReleasedEventArgs e) {
			treeDragPress = null;
			treeDragNode = null;
		}

		void Thumbnails_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e) {
			if (ApplicationHelpers.MainWindow != null && ApplicationHelpers.MainWindowDataContext != null)
				ApplicationHelpers.MainWindowDataContext.Thumbnails_ValueChanged(sender, e);
		}

		void MainWindow_Startup(object? sender, ControlledApplicationLifetimeStartupEventArgs e) {
			var vm = ApplicationHelpers.MainWindowDataContext;
			vm.LoadDatabase();
			vm.RestoreBackupScanResults();
		}

		void OnLoadingRowGroup(object? sender, DataGridRowGroupHeaderEventArgs e) {
			var header = e.RowGroupHeader;
			// Avoid adding buttons twice (recycled headers)
			if (header.Tag is true) return;
			header.Tag = true;
			// The summary below replaces the raw key; "ItemInfo.GroupId:" adds nothing.
			header.IsPropertyNameVisible = false;

			var vm = ApplicationHelpers.MainWindowDataContext;

			Guid GetGroupId() {
				if (header.DataContext is Avalonia.Collections.DataGridCollectionViewGroup g) {
					var first = g.Items.OfType<DuplicateItemVM>().FirstOrDefault();
					if (first != null) return first.ItemInfo.GroupId;
				}
				return Guid.Empty;
			}

			var compareBtn = new Button { Content = "Compare", Classes = { "group-action" } };
			var keepBestBtn = new Button { Content = "Keep Best", Classes = { "group-action" } };

			compareBtn.Click += (_, _) => {
				var id = GetGroupId();
				if (id != Guid.Empty) vm.CompareGroup(id);
			};
			keepBestBtn.Click += (_, _) => {
				var id = GetGroupId();
				if (id != Guid.Empty) vm.KeepBestInGroup(id);
			};

			// Two per-group checkboxes: "check all rows" (composes with every checked-items
			// menu) and "include in compare-in-player" (vm.CompareIncludedGroups whitelist).
			var checkAllBox = new CheckBox { Content = App.Lang["GroupHeader.CheckAll"], VerticalAlignment = VerticalAlignment.Center, MinWidth = 0 };
			ToolTip.SetTip(checkAllBox, App.Lang["GroupHeader.CheckAllTooltip"]);
			var tourBox = new CheckBox { Content = App.Lang["GroupHeader.TourSelect"], VerticalAlignment = VerticalAlignment.Center, MinWidth = 0 };
			ToolTip.SetTip(tourBox, App.Lang["GroupHeader.TourSelectTooltip"]);

			// Recycled headers get a new group: box state must always be derived from the
			// live DataContext, and programmatic syncs must not write back into the data.
			bool syncingBoxes = false;
			bool? lastCheckAllState = false; // last rendered state; a click leaving indeterminate means "check all"
			void SyncGroupBoxes() {
				syncingBoxes = true;
				try {
					if (header.DataContext is Avalonia.Collections.DataGridCollectionViewGroup g) {
						int total = 0, done = 0;
						foreach (var item in g.Items.OfType<DuplicateItemVM>()) {
							total++;
							if (item.Checked) done++;
						}
						checkAllBox.IsChecked = total == 0 || done == 0 ? false : done == total ? true : (bool?)null;
						lastCheckAllState = checkAllBox.IsChecked;
					}
					tourBox.IsChecked = vm.CompareIncludedGroups.Contains(GetGroupId());
				}
				finally { syncingBoxes = false; }
			}
			checkAllBox.IsCheckedChanged += (_, _) => {
				if (syncingBoxes) return;
				var id = GetGroupId();
				if (id == Guid.Empty) return;
				// Avalonia toggles indeterminate -> unchecked, but on a master checkbox a
				// click on a partially-checked group must mean "check all", not "wipe".
				bool check = lastCheckAllState == null || checkAllBox.IsChecked == true;
				if (checkAllBox.IsChecked != check) {
					syncingBoxes = true;
					try { checkAllBox.IsChecked = check; } finally { syncingBoxes = false; }
				}
				lastCheckAllState = check;
				vm.SetGroupChecked(id, check);
			};
			tourBox.IsCheckedChanged += (_, _) => {
				if (syncingBoxes) return;
				var id = GetGroupId();
				if (id == Guid.Empty) return;
				if (tourBox.IsChecked == true) vm.CompareIncludedGroups.Add(id);
				else vm.CompareIncludedGroups.Remove(id);
			};

			var panel = new StackPanel {
				Orientation = Orientation.Horizontal,
				Spacing = 4,
				Margin = new Thickness(8, 0, 4, 0),
				VerticalAlignment = VerticalAlignment.Center,
				Children = { checkAllBox, tourBox, compareBtn, keepBestBtn }
			};

			// Shown in place of the raw GroupId GUID ("4 files · 3.2 GB").
			var summaryText = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
			void UpdateHeaderSummary() {
				if (header.DataContext is not Avalonia.Collections.DataGridCollectionViewGroup g) return;
				int count = 0;
				long totalSize = 0;
				foreach (var item in g.Items.OfType<DuplicateItemVM>()) {
					count++;
					if (item.ItemInfo.SizeLong > 0)
						totalSize += item.ItemInfo.SizeLong;
				}
				summaryText.Text = string.Format(App.Lang["GroupHeader.Summary"], count, totalSize.BytesToString());
			}
			// Recycled headers keep our injected controls but get a new group.
			header.DataContextChanged += (_, _) => { UpdateHeaderSummary(); SyncGroupBoxes(); };
			// Live re-sync when Checked changes anywhere (menus, presets, undo). Subscribe
			// per attached header, drop on detach so discarded headers don't leak handlers.
			header.Unloaded += (_, _) => vm.CheckedByGroupChanged -= SyncGroupBoxes;

			// Inject buttons into the header's visual tree once it's loaded
			header.Loaded += (_, _) => {
				// Walk visual tree to find the root Grid and append our button panel
				var grid = header.GetVisualDescendants().OfType<Grid>().FirstOrDefault();
				if (grid != null && !grid.Children.Contains(panel)) {
					grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
					Grid.SetColumn(panel, grid.ColumnDefinitions.Count - 1);
					grid.Children.Add(panel);
				}
				// Swap the GUID key TextBlock for the human-readable summary. The key
				// element has no template part name, so it's located by its current text.
				if (summaryText.Parent == null &&
					header.DataContext is Avalonia.Collections.DataGridCollectionViewGroup g) {
					string key = g.Key?.ToString() ?? string.Empty;
					var keyText = header.GetVisualDescendants().OfType<TextBlock>()
						.FirstOrDefault(tb => tb.Text == key);
					if (keyText != null && keyText.Parent is Panel keyPanel) {
						keyText.IsVisible = false;
						keyPanel.Children.Insert(keyPanel.Children.IndexOf(keyText) + 1, summaryText);
					}
				}
				UpdateHeaderSummary();
				vm.CheckedByGroupChanged -= SyncGroupBoxes; // Loaded can refire on re-attach
				vm.CheckedByGroupChanged += SyncGroupBoxes;
				SyncGroupBoxes();
			};
		}

		// ── SWEEP track (slice 1): map the 5 stages onto today's tab bodies ──
		// Sources→Directories(0), Rules→Rules tab(5), Scan→Scanner table(1),
		// Review→card-review tab(4), Reclaim→Reclaim tab(6).
		static readonly int[] StageToTab = { 0, 5, 1, 4, 6 };
		void OnStageClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
			if (sender is Button b && int.TryParse(b.Tag?.ToString(), out int stage))
				SetStage(stage);
		}
		void SetStage(int stage) {
			if (stage < 0 || stage >= StageToTab.Length) return;
			var tabs = this.FindControl<TabControl>("TabControl");
			if (tabs != null) tabs.SelectedIndex = StageToTab[stage];
			var track = this.FindControl<StackPanel>("StageTrack");
			if (track != null)
				foreach (var child in track.Children)
					if (child is Button btn) {
						if (btn.Tag?.ToString() == stage.ToString()) {
							if (!btn.Classes.Contains("active")) btn.Classes.Add("active");
						}
						else btn.Classes.Remove("active");
					}
		}

		// Drawer links (환경설정→Settings tab, 콘솔→Log tab): Tag is a direct tab index.
		// These aren't SWEEP stages, so clear the stage highlight when one is opened.
		void OnGoTab(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
			if (sender is not Button b || !int.TryParse(b.Tag?.ToString(), out int tab)) return;
			var tabs = this.FindControl<TabControl>("TabControl");
			if (tabs != null) tabs.SelectedIndex = tab;
			var track = this.FindControl<StackPanel>("StageTrack");
			if (track != null)
				foreach (var child in track.Children)
					if (child is Button btn) btn.Classes.Remove("active");
		}

		// ── Keep a MAXIMIZED extended-client-area window inside the monitor work area so it
		//    never covers the taskbar (Avalonia 12 removed Win32Properties.AddWndProcHookCallback,
		//    so we subclass the HWND ourselves and chain to the original proc). Windows-only.
		//    The delegate is held in a field so the thunk isn't garbage-collected. ──
		const int WM_GETMINMAXINFO = 0x0024;
		const int GWLP_WNDPROC = -4;
		const int MONITOR_DEFAULTTONEAREST = 0x00000002;
		[StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X; public int Y; }
		[StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
		[StructLayout(LayoutKind.Sequential)] struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
		[StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int CbSize; public NativeRect Monitor; public NativeRect Work; public int Flags; }
		[DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
		[DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);
		[DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
		[DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
		[DllImport("user32.dll")] static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
		[DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
		const uint SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_FRAMECHANGED = 0x20;
		const uint ABM_GETSTATE = 0x4, ABM_GETTASKBARPOS = 0x5, ABS_AUTOHIDE = 0x1;
		const int WM_WINDOWPOSCHANGING = 0x0046;
		[StructLayout(LayoutKind.Sequential)] struct WINDOWPOS { public IntPtr hwnd; public IntPtr hwndInsertAfter; public int x, y, cx, cy; public uint flags; }
		[StructLayout(LayoutKind.Sequential)] struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage; public uint uEdge; public NativeRect rc; public IntPtr lParam; }
		[DllImport("shell32.dll")] static extern uint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

		delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
		WndProc? _subclassProc;   // keep alive
		IntPtr _origWndProc;

		void HookMaximizeToWorkArea() {
			var h = TryGetPlatformHandle();
			if (h == null || h.Handle == IntPtr.Zero) return;
			_subclassProc = SubclassWndProc;
			_origWndProc = GetWindowLongPtr(h.Handle, GWLP_WNDPROC);
			SetWindowLongPtr(h.Handle, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_subclassProc));
		}

		IntPtr SubclassWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam) {
			// Run Avalonia's proc first, then override. Avalonia 12 sets the maximized bounds itself
			// (ignores WM_GETMINMAXINFO), so the authoritative fix is to clamp the actual placement in
			// WM_WINDOWPOSCHANGING; the MINMAXINFO override is kept as a belt-and-suspenders.
			IntPtr result = CallWindowProc(_origWndProc, hWnd, msg, wParam, lParam);
			// While maximized, pin BOTH position and size to the work area (Avalonia otherwise
			// offsets the maximized window off-screen and oversizes it, covering the taskbar).
			if (msg == WM_WINDOWPOSCHANGING && WindowState == WindowState.Maximized
					&& ComputeMaxBounds(hWnd, out int x, out int y, out int w, out int h, out _)) {
				var wp = Marshal.PtrToStructure<WINDOWPOS>(lParam);
				if (wp.x != x || wp.y != y || wp.cx != w || wp.cy != h || (wp.flags & (SWP_NOSIZE | SWP_NOMOVE)) != 0) {
					wp.x = x; wp.y = y; wp.cx = w; wp.cy = h;
					wp.flags &= ~(SWP_NOSIZE | SWP_NOMOVE);
					Marshal.StructureToPtr(wp, lParam, true);
				}
			}
			else if (msg == WM_GETMINMAXINFO && ComputeMaxBounds(hWnd, out int mx, out int my, out int mw, out int mh, out var mi2)) {
				var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
				mmi.MaxPosition.X = mx - mi2.Monitor.Left; mmi.MaxPosition.Y = my - mi2.Monitor.Top;
				mmi.MaxSize.X = mw; mmi.MaxSize.Y = mh;
				Marshal.StructureToPtr(mmi, lParam, true);
			}
			return result;
		}

		// Work area of the window's monitor in absolute screen coords, minus 1px on an auto-hide
		// taskbar's edge so the bar can still reveal (Windows suppresses it under a full-monitor window).
		bool ComputeMaxBounds(IntPtr hWnd, out int x, out int y, out int w, out int h, out MonitorInfo mi) {
			x = y = w = h = 0; mi = default;
			IntPtr mon = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
			if (mon == IntPtr.Zero) return false;
			mi = new MonitorInfo { CbSize = Marshal.SizeOf<MonitorInfo>() };
			if (!GetMonitorInfo(mon, ref mi)) return false;
			x = mi.Work.Left; y = mi.Work.Top; w = mi.Work.Right - mi.Work.Left; h = mi.Work.Bottom - mi.Work.Top;
			bool full = mi.Work.Left == mi.Monitor.Left && mi.Work.Top == mi.Monitor.Top &&
						mi.Work.Right == mi.Monitor.Right && mi.Work.Bottom == mi.Monitor.Bottom;
			if (full) {
				var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
				if ((SHAppBarMessage(ABM_GETSTATE, ref abd) & ABS_AUTOHIDE) != 0) {
					var pb = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
					SHAppBarMessage(ABM_GETTASKBARPOS, ref pb);
					switch (pb.uEdge) { case 0: x += 1; w -= 1; break; case 1: y += 1; h -= 1; break; case 2: w -= 1; break; default: h -= 1; break; }
				}
			}
			return true;
		}

		void OnConsoleToggle(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
			if (DataContext is ViewModels.MainWindowVM vm) vm.ConsoleOpen = !vm.ConsoleOpen;
		}

		void InitializeComponent() => AvaloniaXamlLoader.Load(this);
	}
}
