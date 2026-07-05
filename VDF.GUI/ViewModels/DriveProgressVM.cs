using System.Collections.ObjectModel;
using Avalonia.Media;
using ReactiveUI;
using VDF.GUI.Data;

namespace VDF.GUI.ViewModels {
	/// <summary>
	/// One drive's segment in the per-drive scan progress bar. <see cref="Weight"/> (total bytes on that
	/// drive) drives the segment width; <see cref="Fraction"/> (0..1) drives its fill; <see cref="Fill"/>
	/// is the drive's distinct colour. Built once per drive when a scan starts; only Fraction/Label change.
	/// </summary>
	public sealed class DriveProgressVM : ReactiveObject {
		public string Root { get; }
		public double Weight { get; }
		public IBrush Fill { get; }

		public DriveProgressVM(string root, IBrush fill, double weight) {
			Root = root;
			Fill = fill;
			Weight = weight <= 0 ? 1 : weight;
		}

		double _Fraction;
		public double Fraction {
			get => _Fraction;
			set => this.RaiseAndSetIfChanged(ref _Fraction, value);
		}

		string _Label = string.Empty;
		public string Label {
			get => _Label;
			set => this.RaiseAndSetIfChanged(ref _Label, value);
		}

		// One "now processing" line per worker currently active on this drive — grows/shrinks with the
		// drive's live concurrency, so 2+ workers show 2+ lines. Rebuilt each progress tick.
		public ObservableCollection<string> ActiveFileLines { get; } = new();

		// Per-drive parallelism override, chosen live from the in-bar dropdown. CapIndex is the ComboBox
		// selection; it maps to an actual worker cap (0 = auto), is pushed to the running scan via SetCap
		// and persisted per drive root so the choice survives across scans and sessions.
		static readonly int[] CapValues = { 0, 1, 2, 3, 4, 6, 8 };
		public static int CapIndexFor(int cap) {
			for (int i = 0; i < CapValues.Length; i++) if (CapValues[i] == cap) return i;
			return 0;
		}
		public System.Action<string, int>? SetCap;
		int _CapIndex;
		public int CapIndex {
			get => _CapIndex;
			set {
				this.RaiseAndSetIfChanged(ref _CapIndex, value);
				int i = value < 0 || value >= CapValues.Length ? 0 : value;
				int cap = CapValues[i];
				SetCap?.Invoke(Root, cap);
				var caps = SettingsFile.Instance.DriveParallelismCaps;
				if (cap == 0) caps.Remove(Root); else caps[Root] = cap;
			}
		}

		// Per-drive pause checkbox (leftmost in the bar). Deliberately session-only —
		// a new scan always starts with every drive active, so a forgotten unchecked
		// box can't silently exclude a drive next time. An unchecked box is re-pushed
		// every progress tick (see UpdateDriveSegments) because the engine recreates
		// its DriveCounters at each scan/stage start.
		public System.Action<string, bool>? SetEnabled;
		bool _IsActive = true;
		public bool IsActive {
			get => _IsActive;
			set {
				this.RaiseAndSetIfChanged(ref _IsActive, value);
				SetEnabled?.Invoke(Root, value);
			}
		}
	}
}
