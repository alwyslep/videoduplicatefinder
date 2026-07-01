using Avalonia;
using Avalonia.Controls;

namespace VDF.GUI {
	/// <summary>
	/// Lays out children horizontally, giving each a slice of the available width proportional to its
	/// attached <see cref="WeightProperty"/> value. Used by the per-drive segmented scan progress bar:
	/// each drive's segment is as wide as that drive's share of total bytes to process, and the whole
	/// row stretches to the container (window) width. Zero total weight falls back to an equal split.
	/// </summary>
	public sealed class WeightedStackPanel : Panel {
		public static readonly AttachedProperty<double> WeightProperty =
			AvaloniaProperty.RegisterAttached<WeightedStackPanel, Control, double>("Weight", 1d);

		public static double GetWeight(Control c) => c.GetValue(WeightProperty);
		public static void SetWeight(Control c, double value) => c.SetValue(WeightProperty, value);

		protected override Size MeasureOverride(Size availableSize) {
			double height = 0;
			foreach (var child in Children) {
				child.Measure(new Size(0, availableSize.Height));
				if (child.DesiredSize.Height > height) height = child.DesiredSize.Height;
			}
			double width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
			return new Size(width, height);
		}

		protected override Size ArrangeOverride(Size finalSize) {
			int n = Children.Count;
			if (n == 0) return finalSize;

			double total = 0;
			foreach (var child in Children) {
				double w = GetWeight(child);
				if (w > 0) total += w;
			}

			double x = 0;
			for (int i = 0; i < n; i++) {
				var child = Children[i];
				double w = GetWeight(child);
				double slice = total > 0 ? finalSize.Width * (w > 0 ? w : 0) / total : finalSize.Width / n;
				// Give the last child the exact remainder so rounding never leaves a sliver gap/overflow.
				if (i == n - 1) slice = System.Math.Max(0, finalSize.Width - x);
				child.Arrange(new Rect(x, 0, slice, finalSize.Height));
				x += slice;
			}
			return finalSize;
		}
	}
}
