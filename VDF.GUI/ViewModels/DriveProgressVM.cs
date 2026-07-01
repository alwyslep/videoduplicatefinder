using Avalonia.Media;
using ReactiveUI;

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
	}
}
