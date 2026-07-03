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

		// "Now processing" row under the status bar: the file (+ sub-stage) a worker on this drive
		// is currently reading. Empty hides the row, so the row count follows the active drives.
		string _CurrentFileText = string.Empty;
		public string CurrentFileText {
			get => _CurrentFileText;
			set => this.RaiseAndSetIfChanged(ref _CurrentFileText, value);
		}

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
	}
}
