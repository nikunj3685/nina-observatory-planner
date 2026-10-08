using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace NINA.ObservatoryPlanner.UI {

    /// <summary>Visible when the bound value's text equals the parameter (e.g. ConstraintBy == "Time").</summary>
    public sealed class EqualsVisibleConverter : IValueConverter {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    public sealed class InvertBool : IValueConverter {
        public static readonly InvertBool Instance = new();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b ? !b : value;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b ? !b : value;
    }

    /// <summary>Collapsed for null or empty text.</summary>
    public sealed class TextVisibleConverter : IValueConverter {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    /// <summary>Collapsed when true (the opposite of BooleanToVisibilityConverter).</summary>
    public sealed class BoolCollapsedConverter : IValueConverter {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? Visibility.Collapsed : Visibility.Visible;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    /// <summary>Radio buttons for an enum: checked when the value's name equals the parameter.</summary>
    public sealed class EnumBoolConverter : IValueConverter {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) {
            var type = Nullable.GetUnderlyingType(targetType) ?? targetType;
            return value is true && type.IsEnum ? Enum.Parse(type, parameter.ToString()) : Binding.DoNothing;
        }
    }

    /// <summary>Row number from ItemsControl.AlternationIndex.</summary>
    public sealed class PlusOneConverter : IValueConverter {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is int i ? i + 1 : value;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    /// <summary>Fraction of a progress bar: values Done, Count → 0..1 for a ScaleTransform.</summary>
    public sealed class FractionConverter : IMultiValueConverter {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) {
            if (values.Length < 2 || values[0] is not IConvertible a || values[1] is not IConvertible b) { return 0.0; }
            var total = System.Convert.ToDouble(b, culture);
            return total <= 0 ? 0.0 : Math.Max(0, Math.Min(1, System.Convert.ToDouble(a, culture) / total));
        }
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => null;
    }

    /// <summary>Which target the run is imaging now, and which one it is paused on (for the target list).</summary>
    public sealed record TargetListState(Guid? Imaging, Guid? Paused);

    /// <summary>
    /// Status of a row in the target list: ▶ Imaging (the run is on it now), ‖ Paused (checked, not being imaged; orange when
    /// the run is paused on it), ■ Stopped (unchecked, or nothing to take), ✔ Complete. The parameter "brush" gives the
    /// colour, "tip" the tooltip text, otherwise the icon.
    /// </summary>
    public sealed class TargetStateConverter : IMultiValueConverter {
        public static (string Icon, string Brush, string Tip) StateOf(Core.PlannerTarget t, TargetListState state) {
            if (t == null) { return ("", "OP_Muted", null); }
            if (t.IsComplete) { return ("✔", "OP_Safe", "Complete: all frames are taken"); }
            if (state?.Imaging == t.Id) { return ("▶", "OP_Accent", "Imaging: the run is on this target now"); }
            if (state?.Paused == t.Id) { return ("‖", "OP_Accent", "Paused: the run is paused on this target; Start sequence continues with it"); }
            if (!t.Enabled) { return ("■", "OP_Muted", "Stopped: not checked"); }
            if (t.TotalFrames == 0) { return ("■", "OP_Muted", "Stopped: no exposures to take (no row is ticked, or every Repeat is 0)"); }
            return ("‖", "OP_Muted", "Paused: checked, not being imaged now");
        }

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) {
            var (icon, key, tip) = StateOf(values.Length > 0 ? values[0] as Core.PlannerTarget : null, values.Length > 1 ? values[1] as TargetListState : null);
            return (parameter as string) switch {
                "brush" => Application.Current?.TryFindResource(key) as System.Windows.Media.Brush ?? PlannerBrushes.ByKey(key),
                "tip" => tip,
                _ => icon
            };
        }
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => null;
    }

    /// <summary>The mockup's semantic colours, for code that runs outside the resource tree.</summary>
    public static class PlannerBrushes {
        public static readonly System.Windows.Media.SolidColorBrush Safe = Frozen(0x57, 0xB9, 0x7C);
        public static readonly System.Windows.Media.SolidColorBrush Info = Frozen(0x6A, 0xA7, 0xD8);
        public static readonly System.Windows.Media.SolidColorBrush Accent = Frozen(0xE3, 0xA6, 0x46);
        public static readonly System.Windows.Media.SolidColorBrush Unsafe = Frozen(0xE0, 0x64, 0x5C);
        public static readonly System.Windows.Media.SolidColorBrush Muted = Frozen(0x8B, 0x93, 0x9C);

        public static System.Windows.Media.Brush ByKey(string key) => key switch {
            "OP_Safe" => Safe, "OP_Info" => Info, "OP_Accent" => Accent, "OP_Unsafe" => Unsafe, _ => Muted
        };

        private static System.Windows.Media.SolidColorBrush Frozen(byte r, byte g, byte b) {
            var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
