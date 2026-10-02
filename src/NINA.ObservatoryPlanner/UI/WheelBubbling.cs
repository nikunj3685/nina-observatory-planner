using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NINA.ObservatoryPlanner.UI {

    /// <summary>
    /// A scroll area inside the scrolling page (a stage list, the target list, the exposure rows) keeps the mouse wheel
    /// while it can scroll that way. With nothing to scroll, or once it reaches its top or bottom, the wheel goes on to
    /// the page, so the page scrolls wherever the mouse is.
    /// </summary>
    public static class WheelBubbling {

        public static readonly DependencyProperty AtEndsProperty = DependencyProperty.RegisterAttached(
            "AtEnds", typeof(bool), typeof(WheelBubbling), new PropertyMetadata(false, Changed));

        public static bool GetAtEnds(DependencyObject d) => (bool)d.GetValue(AtEndsProperty);
        public static void SetAtEnds(DependencyObject d, bool value) => d.SetValue(AtEndsProperty, value);

        private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) {
            if (d is not UIElement element) { return; }
            element.PreviewMouseWheel -= OnPreviewMouseWheel;
            if (e.NewValue is true) { element.PreviewMouseWheel += OnPreviewMouseWheel; }
        }

        private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e) {
            if (e.Handled || sender is not DependencyObject element) { return; }
            if (CanScroll(ScrollerOf(element), e.Delta)) { return; }
            e.Handled = true;
            if (VisualTreeHelper.GetParent(element) is UIElement parent) {
                parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = sender });
            }
        }

        /// <summary>Whether the scroll area can still move in the wheel's direction (positive delta = up).</summary>
        public static bool CanScroll(ScrollViewer sv, int delta) {
            if (sv == null || sv.ScrollableHeight <= 0) { return false; }
            return delta > 0 ? sv.VerticalOffset > 0 : sv.VerticalOffset < sv.ScrollableHeight;
        }

        private static ScrollViewer ScrollerOf(DependencyObject d) {
            if (d is ScrollViewer sv) { return sv; }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) {
                var found = ScrollerOf(VisualTreeHelper.GetChild(d, i));
                if (found != null) { return found; }
            }
            return null;
        }
    }
}
