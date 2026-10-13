using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace NINA.ObservatoryPlanner.UI {

    /// <summary>
    /// Shows the first child that fits the available width, the others are hidden. The last child is the fallback.
    /// Used where a row of buttons turns into a menu, or labels into shorter ones, when the panel is narrow.
    /// </summary>
    public class FirstFitPanel : Panel {
        private static readonly DependencyPropertyKey ChosenIndexKey = DependencyProperty.RegisterReadOnly(
            nameof(ChosenIndex), typeof(int), typeof(FirstFitPanel), new PropertyMetadata(0));

        public static readonly DependencyProperty ChosenIndexProperty = ChosenIndexKey.DependencyProperty;

        /// <summary>Index of the child shown now.</summary>
        public int ChosenIndex => (int)GetValue(ChosenIndexProperty);

        protected override Size MeasureOverride(Size available) {
            var children = InternalChildren;
            if (children.Count == 0) { return new Size(); }

            var chosen = children.Count - 1;
            for (var i = 0; i < children.Count; i++) {
                children[i].Measure(new Size(double.PositiveInfinity, available.Height));
                if (chosen == children.Count - 1 && i < children.Count - 1 && children[i].DesiredSize.Width <= available.Width + 0.5) {
                    chosen = i;
                }
            }

            // only the chosen child is shown; hidden children still take part in measuring
            for (var i = 0; i < children.Count; i++) {
                var visibility = i == chosen ? Visibility.Visible : Visibility.Hidden;
                if (children[i].Visibility != visibility) { children[i].Visibility = visibility; }
            }
            if (ChosenIndex != chosen) { SetValue(ChosenIndexKey, chosen); }

            children[chosen].Measure(available);
            var desired = children[chosen].DesiredSize;
            return new Size(double.IsInfinity(available.Width) ? desired.Width : Math.Min(desired.Width, available.Width), desired.Height);
        }

        protected override Size ArrangeOverride(Size final) {
            for (var i = 0; i < InternalChildren.Count; i++) {
                var child = InternalChildren[i];
                child.Arrange(i == ChosenIndex ? new Rect(final) : new Rect(0, 0, 0, 0));
            }
            return final;
        }
    }

    /// <summary>How an exposure row is laid out at the width it has.</summary>
    public enum ExposureRowMode {
        /// <summary>Everything on one line.</summary>
        OneLine,
        /// <summary>The fields on one line, the progress bar on a thin line under them.</summary>
        ProgressBelow,
        /// <summary>Binning and Repeat move to a second line, the progress bar to a third.</summary>
        TwoLines,
        /// <summary>The narrowest: three lines, the progress bar next to Repeat.</summary>
        ThreeLines,
    }

    /// <summary>
    /// One exposure row (or the header row): Priority, Run, Type, Filter, Exposure, Binning, Repeat, Progress, Delete.
    /// The row stays on one line while it fits; otherwise the progress bar moves under the row, and narrower still the
    /// fields wrap. Every row (and the header) has the same width and the same column sizes, so all of them pick the same
    /// layout and the columns line up. Type, Filter and Binning share any spare width; Exposure and Repeat keep the size
    /// of four digits and their up/down buttons.
    /// </summary>
    public class ExposureRowPanel : Panel {
        // column sizes in the panel's units (the whole panel is scaled for the text size)
        public const double PriorityWidth = 58, RunWidth = 24, TypeWidth = 76, NumberWidth = 72, BinningWidth = 64, ProgressWidth = 72, DeleteWidth = 24;
        public const double Gap = 6, LineGap = 4;

        public static readonly DependencyProperty FilterCharsProperty = DependencyProperty.RegisterAttached(
            "FilterChars", typeof(int), typeof(ExposureRowPanel),
            new FrameworkPropertyMetadata(5, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));

        /// <summary>Characters the Filter column must fit: the longest filter name plus one (inherited from the table).</summary>
        public static int GetFilterChars(DependencyObject d) => (int)d.GetValue(FilterCharsProperty);
        public static void SetFilterChars(DependencyObject d, int value) => d.SetValue(FilterCharsProperty, value);

        public static readonly DependencyProperty ProgressOnOwnLineProperty = DependencyProperty.RegisterAttached(
            "ProgressOnOwnLine", typeof(bool), typeof(ExposureRowPanel), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

        /// <summary>Set on the progress cell when it has a line of its own, so it can draw a thin bar.</summary>
        public static bool GetProgressOnOwnLine(DependencyObject d) => (bool)d.GetValue(ProgressOnOwnLineProperty);
        public static void SetProgressOnOwnLine(DependencyObject d, bool value) => d.SetValue(ProgressOnOwnLineProperty, value);

        public static readonly DependencyProperty IsHeaderProperty = DependencyProperty.Register(
            nameof(IsHeader), typeof(bool), typeof(ExposureRowPanel), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

        /// <summary>The header row: its Progress label is left out once the progress bar is under the row.</summary>
        public bool IsHeader { get => (bool)GetValue(IsHeaderProperty); set => SetValue(IsHeaderProperty, value); }

        private static readonly DependencyPropertyKey ModeKey = DependencyProperty.RegisterReadOnly(
            nameof(Mode), typeof(ExposureRowMode), typeof(ExposureRowPanel), new PropertyMetadata(ExposureRowMode.OneLine));

        public static readonly DependencyProperty ModeProperty = ModeKey.DependencyProperty;

        public ExposureRowMode Mode => (ExposureRowMode)GetValue(ModeProperty);

        private const int P = 0, R = 1, T = 2, F = 3, E = 4, B = 5, N = 6, G = 7, X = 8;

        /// <summary>A child placed in a span of columns on one line.</summary>
        private readonly record struct Cell(int Child, int Line, int FirstColumn, int LastColumn);

        /// <summary>The columns of a layout (minimum widths and how spare width is shared) and where each child goes.</summary>
        private sealed record Plan(double[] Min, double[] Weight, Cell[] Cells, int Lines);

        /// <summary>Width the Filter column needs for <paramref name="chars"/> characters plus the drop-down arrow.</summary>
        public static double FilterWidth(int chars, Visual reference = null) {
            var dip = reference == null ? 1.0 : VisualTreeHelper.GetDpi(reference).PixelsPerDip;
            var text = new FormattedText(new string('0', Math.Max(1, chars)), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 12, Brushes.Black, dip);
            return Math.Ceiling(text.WidthIncludingTrailingWhitespace) + 30;
        }

        /// <summary>The layout a row of <paramref name="width"/> gets.</summary>
        public static ExposureRowMode ModeFor(double width, double filterWidth) {
            if (double.IsInfinity(width)) { return ExposureRowMode.OneLine; }
            double Sum(params double[] w) => w.Sum() + (w.Length - 1) * Gap;
            if (width >= Sum(PriorityWidth, RunWidth, TypeWidth, filterWidth, NumberWidth, BinningWidth, NumberWidth, ProgressWidth, DeleteWidth) - 0.5) { return ExposureRowMode.OneLine; }
            if (width >= Sum(PriorityWidth, RunWidth, TypeWidth, filterWidth, NumberWidth, BinningWidth, NumberWidth, DeleteWidth) - 0.5) { return ExposureRowMode.ProgressBelow; }
            if (width >= Sum(PriorityWidth, RunWidth, TypeWidth, Math.Max(filterWidth, NumberWidth), NumberWidth, DeleteWidth) - 0.5) { return ExposureRowMode.TwoLines; }
            return ExposureRowMode.ThreeLines;
        }

        private Plan PlanFor(ExposureRowMode mode, double filterWidth) {
            switch (mode) {
                case ExposureRowMode.OneLine:
                    return new Plan(
                        new[] { PriorityWidth, RunWidth, TypeWidth, filterWidth, NumberWidth, BinningWidth, NumberWidth, ProgressWidth, DeleteWidth },
                        new[] { 0, 0, 1, 1.3, 0, 0.8, 0, 1.4, 0 },
                        Enumerable.Range(0, 9).Select(i => new Cell(i, 0, i, i)).ToArray(), 1);
                case ExposureRowMode.ProgressBelow:
                    return new Plan(
                        new[] { PriorityWidth, RunWidth, TypeWidth, filterWidth, NumberWidth, BinningWidth, NumberWidth, DeleteWidth },
                        new[] { 0, 0, 1, 1.3, 0, 0.8, 0, 0 },
                        new[] { new Cell(P, 0, 0, 0), new Cell(R, 0, 1, 1), new Cell(T, 0, 2, 2), new Cell(F, 0, 3, 3), new Cell(E, 0, 4, 4),
                            new Cell(B, 0, 5, 5), new Cell(N, 0, 6, 6), new Cell(X, 0, 7, 7), new Cell(G, 1, 2, 7) }, 2);
                case ExposureRowMode.TwoLines:
                    return new Plan(
                        new[] { PriorityWidth, RunWidth, TypeWidth, Math.Max(filterWidth, NumberWidth), NumberWidth, DeleteWidth },
                        new[] { 0, 0, 1, 1.3, 0, 0 },
                        new[] { new Cell(P, 0, 0, 0), new Cell(R, 0, 1, 1), new Cell(T, 0, 2, 2), new Cell(F, 0, 3, 3), new Cell(E, 0, 4, 4), new Cell(X, 0, 5, 5),
                            new Cell(B, 1, 2, 2), new Cell(N, 1, 3, 3), new Cell(G, 2, 2, 5) }, 3);
                default:
                    return new Plan(
                        new[] { PriorityWidth, RunWidth, Math.Max(TypeWidth, NumberWidth), Math.Max(filterWidth, BinningWidth), DeleteWidth },
                        new[] { 0, 0, 1, 1.3, 0 },
                        new[] { new Cell(P, 0, 0, 0), new Cell(R, 0, 1, 1), new Cell(T, 0, 2, 2), new Cell(F, 0, 3, 3), new Cell(X, 0, 4, 4),
                            new Cell(E, 1, 2, 2), new Cell(B, 1, 3, 3), new Cell(N, 2, 2, 2), new Cell(G, 2, 3, 4) }, 3);
            }
        }

        /// <summary>Column positions and widths for <paramref name="width"/>, spare width shared by the columns' weights.</summary>
        private static (double[] X, double[] W) Columns(Plan plan, double width) {
            var min = plan.Min;
            var w = (double[])min.Clone();
            var spare = double.IsInfinity(width) ? 0 : width - min.Sum() - (min.Length - 1) * Gap;
            var weights = plan.Weight.Sum();
            if (spare > 0 && weights > 0) {
                for (var i = 0; i < w.Length; i++) { w[i] += spare * plan.Weight[i] / weights; }
            }
            var x = new double[w.Length];
            for (var i = 1; i < w.Length; i++) { x[i] = x[i - 1] + w[i - 1] + Gap; }
            return (x, w);
        }

        private Plan current;
        private double[] colX, colW, lineY, lineH;

        protected override Size MeasureOverride(Size available) {
            var children = InternalChildren;
            var filterWidth = FilterWidth(GetFilterChars(this) + 0, this);
            var mode = ModeFor(available.Width, filterWidth);
            if (Mode != mode) { SetValue(ModeKey, mode); }

            current = PlanFor(mode, filterWidth);
            (colX, colW) = Columns(current, available.Width);

            if (children.Count > G) { SetProgressOnOwnLine(children[G], mode is ExposureRowMode.ProgressBelow or ExposureRowMode.TwoLines); }

            lineH = new double[current.Lines];
            var placed = new HashSet<int>();
            foreach (var cell in current.Cells) {
                if (cell.Child >= children.Count) { continue; }
                var child = children[cell.Child];
                var hidden = IsHeader && cell.Child == G && mode != ExposureRowMode.OneLine;
                var visibility = hidden ? Visibility.Hidden : Visibility.Visible;
                if (child.Visibility != visibility) { child.Visibility = visibility; }
                placed.Add(cell.Child);
                if (hidden) { child.Measure(new Size(0, 0)); continue; }
                var width = colX[cell.LastColumn] + colW[cell.LastColumn] - colX[cell.FirstColumn];
                child.Measure(new Size(width, double.PositiveInfinity));
                lineH[cell.Line] = Math.Max(lineH[cell.Line], child.DesiredSize.Height);
            }
            for (var i = 0; i < children.Count; i++) {
                if (!placed.Contains(i)) { children[i].Measure(new Size(0, 0)); }
            }

            lineY = new double[current.Lines];
            var height = 0.0;
            for (var l = 0; l < current.Lines; l++) {
                if (lineH[l] <= 0) { lineY[l] = height; continue; }
                if (height > 0) { height += LineGap; }
                lineY[l] = height;
                height += lineH[l];
            }
            var total = colX[colX.Length - 1] + colW[colW.Length - 1];
            return new Size(double.IsInfinity(available.Width) ? total : Math.Min(available.Width, total), height);
        }

        protected override Size ArrangeOverride(Size final) {
            var children = InternalChildren;
            if (current == null) { return final; }
            if (Math.Abs(final.Width - (colX[colX.Length - 1] + colW[colW.Length - 1])) > 0.5) { (colX, colW) = Columns(current, final.Width); }
            var placed = new HashSet<int>();
            foreach (var cell in current.Cells) {
                if (cell.Child >= children.Count) { continue; }
                placed.Add(cell.Child);
                var x = colX[cell.FirstColumn];
                var width = colX[cell.LastColumn] + colW[cell.LastColumn] - x;
                children[cell.Child].Arrange(new Rect(x, lineY[cell.Line], Math.Max(0, width), lineH[cell.Line]));
            }
            for (var i = 0; i < children.Count; i++) {
                if (!placed.Contains(i)) { children[i].Arrange(new Rect(0, 0, 0, 0)); }
            }
            return final;
        }
    }

    /// <summary>
    /// The target list and the selected target: side by side when both fit, the list above the details when not.
    /// Side by side the list is as tall as the details and scrolls inside that height.
    /// </summary>
    public class SideBySidePanel : Panel {
        public static readonly DependencyProperty ListWidthProperty = DependencyProperty.Register(
            nameof(ListWidth), typeof(double), typeof(SideBySidePanel), new FrameworkPropertyMetadata(260.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty DetailsMinWidthProperty = DependencyProperty.Register(
            nameof(DetailsMinWidth), typeof(double), typeof(SideBySidePanel), new FrameworkPropertyMetadata(430.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
            nameof(Gap), typeof(double), typeof(SideBySidePanel), new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty IsSideBySideProperty = DependencyProperty.RegisterAttached(
            "IsSideBySide", typeof(bool), typeof(SideBySidePanel), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

        public double ListWidth { get => (double)GetValue(ListWidthProperty); set => SetValue(ListWidthProperty, value); }
        public double DetailsMinWidth { get => (double)GetValue(DetailsMinWidthProperty); set => SetValue(DetailsMinWidthProperty, value); }
        public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }

        /// <summary>Inherited by the list while it sits beside the details: it fills that height instead of showing four rows.</summary>
        public static bool GetIsSideBySide(DependencyObject d) => (bool)d.GetValue(IsSideBySideProperty);
        public static void SetIsSideBySide(DependencyObject d, bool value) => d.SetValue(IsSideBySideProperty, value);

        /// <summary>Side by side the list is never shorter than this, even when the details are short.</summary>
        public const double MinSideHeight = 180;

        private bool side;

        public bool Side => side;

        protected override Size MeasureOverride(Size available) {
            if (InternalChildren.Count < 2) { return new Size(); }
            var list = InternalChildren[0];
            var details = InternalChildren[1];
            side = !double.IsInfinity(available.Width) && available.Width >= ListWidth + Gap + DetailsMinWidth;
            if (GetIsSideBySide(list) != side) { SetIsSideBySide(list, side); }

            if (side) {
                details.Measure(new Size(available.Width - ListWidth - Gap, double.PositiveInfinity));
                var height = Math.Max(details.DesiredSize.Height, MinSideHeight);
                list.Measure(new Size(ListWidth, height));
                return new Size(available.Width, height);
            }
            list.Measure(new Size(available.Width, double.PositiveInfinity));
            details.Measure(new Size(available.Width, double.PositiveInfinity));
            var width = double.IsInfinity(available.Width) ? Math.Max(list.DesiredSize.Width, details.DesiredSize.Width) : available.Width;
            return new Size(width, list.DesiredSize.Height + Gap + details.DesiredSize.Height);
        }

        protected override Size ArrangeOverride(Size final) {
            if (InternalChildren.Count < 2) { return final; }
            var list = InternalChildren[0];
            var details = InternalChildren[1];
            if (side) {
                list.Arrange(new Rect(0, 0, ListWidth, final.Height));
                details.Arrange(new Rect(ListWidth + Gap, 0, Math.Max(0, final.Width - ListWidth - Gap), final.Height));
            } else {
                list.Arrange(new Rect(0, 0, final.Width, list.DesiredSize.Height));
                details.Arrange(new Rect(0, list.DesiredSize.Height + Gap, final.Width, details.DesiredSize.Height));
            }
            return final;
        }
    }

    /// <summary>
    /// Shows at most <see cref="MaxItems"/> rows of the list inside it, then the list scrolls. Inside the target list while
    /// it sits beside the details (<see cref="SideBySidePanel.IsSideBySideProperty"/>) it fills the height it is given instead.
    /// The list needs a non-virtualizing items panel, so every row has a size.
    /// </summary>
    public class CappedScroller : Decorator {
        public static readonly DependencyProperty MaxItemsProperty = DependencyProperty.Register(
            nameof(MaxItems), typeof(int), typeof(CappedScroller), new FrameworkPropertyMetadata(4, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public int MaxItems { get => (int)GetValue(MaxItemsProperty); set => SetValue(MaxItemsProperty, value); }

        static CappedScroller() {
            SideBySidePanel.IsSideBySideProperty.OverrideMetadata(typeof(CappedScroller),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));
        }

        protected override Size MeasureOverride(Size available) {
            if (Child == null) { return new Size(); }
            if (SideBySidePanel.GetIsSideBySide(this) && !double.IsInfinity(available.Height)) {
                Child.Measure(available);
                return new Size(Child.DesiredSize.Width, available.Height);
            }

            Child.Measure(new Size(available.Width, double.PositiveInfinity));
            var full = Child.DesiredSize;
            var host = ItemsHost(Child);
            if (host == null || host.Children.Count <= MaxItems) { return full; }

            var rows = 0.0;
            for (var i = 0; i < MaxItems; i++) { rows += host.Children[i].DesiredSize.Height; }
            var cap = full.Height - host.DesiredSize.Height + rows;
            var height = Math.Min(full.Height, Math.Min(cap, available.Height));
            Child.Measure(new Size(available.Width, height));
            return new Size(Child.DesiredSize.Width, height);
        }

        protected override Size ArrangeOverride(Size final) {
            Child?.Arrange(new Rect(final));
            return final;
        }

        private static Panel ItemsHost(DependencyObject d) {
            if (d is Panel p && p.IsItemsHost) { return p; }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) {
                var found = ItemsHost(VisualTreeHelper.GetChild(d, i));
                if (found != null) { return found; }
            }
            return null;
        }
    }

    /// <summary>Lays its children out in as many columns of at least <see cref="MinColumnWidth"/> as fit; each row is as tall as its tallest child.</summary>
    public class AutoColumnsPanel : Panel {
        public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
            nameof(MinColumnWidth), typeof(double), typeof(AutoColumnsPanel), new FrameworkPropertyMetadata(200.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
            nameof(MaxColumns), typeof(int), typeof(AutoColumnsPanel), new FrameworkPropertyMetadata(int.MaxValue, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty ColumnGapProperty = DependencyProperty.Register(
            nameof(ColumnGap), typeof(double), typeof(AutoColumnsPanel), new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty RowGapProperty = DependencyProperty.Register(
            nameof(RowGap), typeof(double), typeof(AutoColumnsPanel), new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public double MinColumnWidth { get => (double)GetValue(MinColumnWidthProperty); set => SetValue(MinColumnWidthProperty, value); }
        public int MaxColumns { get => (int)GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }
        public double ColumnGap { get => (double)GetValue(ColumnGapProperty); set => SetValue(ColumnGapProperty, value); }
        public double RowGap { get => (double)GetValue(RowGapProperty); set => SetValue(RowGapProperty, value); }

        private int columns = 1;
        private double[] rowHeights = Array.Empty<double>();

        /// <summary>Columns for <paramref name="width"/> and <paramref name="count"/> children.</summary>
        public static int ColumnsFor(double width, int count, double minColumnWidth, double gap, int maxColumns) {
            if (count == 0) { return 1; }
            if (double.IsInfinity(width)) { return Math.Max(1, Math.Min(count, maxColumns)); }
            var fit = (int)Math.Floor((width + gap) / (minColumnWidth + gap));
            return Math.Max(1, Math.Min(Math.Min(fit, count), maxColumns));
        }

        protected override Size MeasureOverride(Size available) {
            var visible = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
            columns = ColumnsFor(available.Width, visible.Count, MinColumnWidth, ColumnGap, MaxColumns);
            var width = double.IsInfinity(available.Width) ? MinColumnWidth * columns + ColumnGap * (columns - 1) : available.Width;
            var columnWidth = (width - ColumnGap * (columns - 1)) / columns;
            var rows = (visible.Count + columns - 1) / columns;
            rowHeights = new double[rows];
            for (var i = 0; i < visible.Count; i++) {
                visible[i].Measure(new Size(columnWidth, double.PositiveInfinity));
                rowHeights[i / columns] = Math.Max(rowHeights[i / columns], visible[i].DesiredSize.Height);
            }
            return new Size(width, rowHeights.Sum() + RowGap * Math.Max(0, rows - 1));
        }

        protected override Size ArrangeOverride(Size final) {
            var visible = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
            var columnWidth = (final.Width - ColumnGap * (columns - 1)) / columns;
            var y = 0.0;
            for (var r = 0; r < rowHeights.Length; r++) {
                for (var c = 0; c < columns; c++) {
                    var i = r * columns + c;
                    if (i >= visible.Count) { break; }
                    visible[i].Arrange(new Rect(c * (columnWidth + ColumnGap), y, columnWidth, rowHeights[r]));
                }
                y += rowHeights[r] + RowGap;
            }
            return final;
        }
    }

    /// <summary>A text that is cut off ("…") shows its full text as a tooltip; a text that fits has no tooltip.</summary>
    public static class TrimTip {
        public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
            "Enabled", typeof(bool), typeof(TrimTip), new PropertyMetadata(false, Changed));

        public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
        public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

        private static readonly DependencyPropertyDescriptor TextDescriptor = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));

        private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) {
            if (d is not TextBlock text) { return; }
            text.SizeChanged -= OnSizeChanged;
            TextDescriptor.RemoveValueChanged(text, OnTextChanged);
            if (e.NewValue is true) {
                text.SizeChanged += OnSizeChanged;
                TextDescriptor.AddValueChanged(text, OnTextChanged);
                Update(text);
            }
        }

        private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Update((TextBlock)sender);

        private static void OnTextChanged(object sender, EventArgs e) => Update((TextBlock)sender);

        /// <summary>Whether <paramref name="text"/> is wider than the space it has.</summary>
        public static bool IsTrimmed(TextBlock text) {
            if (string.IsNullOrEmpty(text.Text) || text.ActualWidth <= 0) { return false; }
            var formatted = new FormattedText(text.Text, CultureInfo.CurrentUICulture, text.FlowDirection,
                new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, Brushes.Black,
                VisualTreeHelper.GetDpi(text).PixelsPerDip);
            return formatted.WidthIncludingTrailingWhitespace > text.ActualWidth - text.Padding.Left - text.Padding.Right + 0.5;
        }

        private static void Update(TextBlock text) {
            if (IsTrimmed(text)) { text.ToolTip = text.Text; } else { text.ClearValue(FrameworkElement.ToolTipProperty); }
        }
    }
}
