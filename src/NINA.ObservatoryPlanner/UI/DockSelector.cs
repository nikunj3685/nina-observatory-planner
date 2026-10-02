using NINA.Core.Utility;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NINA.ObservatoryPlanner.UI {

    /// <summary>
    /// Brings a docked panel's tab to the front. In NINA's dock each tab's DataContext is the dock item (AvalonDock
    /// LayoutAnchorable), whose Content is the panel's view model; selecting/activating that item shows the panel.
    /// </summary>
    internal static class DockSelector {

        public static bool Select(object viewModel, string title) {
            var app = Application.Current;
            if (app == null) { return false; }
            foreach (Window w in app.Windows) {
                foreach (var tab in Descendants(w).OfType<TabItem>()) {
                    var item = tab.DataContext;
                    // The panel's own inner tabs have the view model itself as DataContext: only dock items (with Content) count.
                    if (item == null || ReferenceEquals(item, viewModel)) { continue; }
                    var type = item.GetType();
                    var contentProperty = type.GetProperty("Content");
                    if (contentProperty == null) { continue; }
                    var content = contentProperty.GetValue(item);
                    var itemTitle = type.GetProperty("Title")?.GetValue(item) as string;
                    if (!ReferenceEquals(content, viewModel) && itemTitle != title) { continue; }
                    type.GetProperty("IsSelected")?.SetValue(item, true);
                    type.GetProperty("IsActive")?.SetValue(item, true);
                    tab.IsSelected = true;
                    Logger.Info($"Observatory Planner: brought the panel tab to the front ({type.Name})");
                    return true;
                }
            }
            Logger.Info("Observatory Planner: panel tab not found in the dock");
            return false;
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject root) {
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++) {
                var child = VisualTreeHelper.GetChild(root, i);
                yield return child;
                foreach (var d in Descendants(child)) { yield return d; }
            }
        }
    }
}
