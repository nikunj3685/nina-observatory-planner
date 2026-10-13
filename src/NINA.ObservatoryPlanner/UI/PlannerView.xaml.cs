using NINA.ObservatoryPlanner.Core;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace NINA.ObservatoryPlanner.UI {

    /// <summary>NINA merges exported resource dictionaries; the panel template is found by the "&lt;VM type&gt;_Dockable" key.</summary>
    [Export(typeof(ResourceDictionary))]
    public partial class PlannerView : ResourceDictionary {
        public PlannerView() {
            InitializeComponent();
        }

        /// <summary>Opens a button's menu on left click (＋, Pause, Target Actions, Sequence Actions); the menu gets the panel's DataContext so its commands bind.</summary>
        private void AddButton_Click(object sender, RoutedEventArgs e) {
            if (sender is Button b && b.ContextMenu != null) {
                b.ContextMenu.PlacementTarget = b;
                b.ContextMenu.DataContext = b.DataContext;
                b.ContextMenu.IsOpen = true;
            }
        }

        /// <summary>Closes the "Captured frames" popup of an exposure row (the value is already bound).</summary>
        private void ProgressOk_Click(object sender, RoutedEventArgs e) {
            DependencyObject d = sender as DependencyObject;
            while (d != null && d is not Popup) {
                d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
            }
            if (d is Popup popup) {
                popup.IsOpen = false;
                if (popup.PlacementTarget is ToggleButton toggle) { toggle.IsChecked = false; }
            }
        }

        /// <summary>▲ ▼ next to Exposure, Repeat and Captured frames: Tag is "field|step".</summary>
        private void Step_Click(object sender, RoutedEventArgs e) {
            if (sender is not FrameworkElement button || button.DataContext is not PlannerExposure row || button.Tag is not string tag) { return; }

            var parts = tag.Split('|');
            if (parts.Length != 2 || !int.TryParse(parts[1], out var step)) { return; }

            Step(row, parts[0], step);
        }

        /// <summary>Changes one number of an exposure row by <paramref name="step"/>, kept within what the field allows.</summary>
        public static void Step(PlannerExposure row, string field, int step) {
            switch (field) {
                case "exposure":
                    row.ExposureTime = Math.Max(0, Math.Min(9999, Math.Round(row.ExposureTime) + step));
                    break;
                case "repeat":
                    row.Count = Math.Max(1, Math.Min(9999, row.Count + step));
                    break;
                case "done":
                    row.Done = Math.Max(0, Math.Min(9999, row.Done + step));
                    break;
            }
        }

        private readonly Dictionary<DependencyObject, int> nextWarning = new();

        /// <summary>The red ⚠ in front of the status text: shows the next warning on the tab shown (opening its section if collapsed).</summary>
        private void JumpToWarning_Click(object sender, RoutedEventArgs e) {
            if (sender is not DependencyObject start) { return; }

            var root = Ancestors(start).OfType<FrameworkElement>().FirstOrDefault(f => Equals(f.Tag, "OP_Root"));
            var tabs = root == null ? null : Descendants(root).OfType<TabControl>().FirstOrDefault();
            if (tabs == null) { return; }

            var warnings = WarningsShown(tabs);
            if (warnings.Count == 0) { return; }

            nextWarning.TryGetValue(tabs, out var index);
            var warning = warnings[index % warnings.Count];
            nextWarning[tabs] = index + 1;

            foreach (var expander in Ancestors(warning).OfType<Expander>()) { expander.IsExpanded = true; }
            warning.Dispatcher.BeginInvoke(new Action(() => {
                warning.BringIntoView();
                Flash(warning);
            }), DispatcherPriority.Loaded);
        }

        /// <summary>Warning boxes (Tag "OP_Warn") on the tab shown, also inside collapsed sections, in page order.</summary>
        public static List<FrameworkElement> WarningsShown(TabControl tabs) =>
            Descendants(tabs).OfType<FrameworkElement>()
                .Where(f => Equals(f.Tag, "OP_Warn") && f.Visibility == Visibility.Visible && ShownOrCollapsedSection(f, tabs))
                .ToList();

        /// <summary>Every element up to <paramref name="stop"/> is visible, except the content of a collapsed section.</summary>
        private static bool ShownOrCollapsedSection(DependencyObject d, DependencyObject stop) {
            var collapsedSection = Ancestors(d).OfType<Expander>().Any(x => !x.IsExpanded);
            foreach (var a in Ancestors(d)) {
                if (ReferenceEquals(a, stop)) { return true; }
                if (a is UIElement u && u.Visibility != Visibility.Visible && !collapsedSection) { return false; }
            }
            return true;
        }

        private static void Flash(UIElement element) {
            var blink = new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(220)) { AutoReverse = true, RepeatBehavior = new RepeatBehavior(2) };
            element.BeginAnimation(UIElement.OpacityProperty, blink);
        }

        private static IEnumerable<DependencyObject> Ancestors(DependencyObject d) {
            for (var p = Parent(d); p != null; p = Parent(p)) { yield return p; }
        }

        private static DependencyObject Parent(DependencyObject d) =>
            d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

        private static IEnumerable<DependencyObject> Descendants(DependencyObject d) {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) {
                var child = VisualTreeHelper.GetChild(d, i);
                yield return child;
                foreach (var grandchild in Descendants(child)) { yield return grandchild; }
            }
        }
    }
}
