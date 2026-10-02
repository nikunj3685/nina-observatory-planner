using System.ComponentModel.Composition;
using System.Windows;

namespace NINA.ObservatoryPlanner.UI {

    /// <summary>NINA merges exported resource dictionaries; the panel template is found by the "&lt;VM type&gt;_Dockable" key.</summary>
    [Export(typeof(ResourceDictionary))]
    public partial class PlannerView : ResourceDictionary {
        public PlannerView() {
            InitializeComponent();
        }

        /// <summary>Opens the ＋ menu on left click; the menu gets the panel's DataContext so its commands bind.</summary>
        private void AddButton_Click(object sender, RoutedEventArgs e) {
            if (sender is System.Windows.Controls.Button b && b.ContextMenu != null) {
                b.ContextMenu.PlacementTarget = b;
                b.ContextMenu.DataContext = b.DataContext;
                b.ContextMenu.IsOpen = true;
            }
        }

        /// <summary>Closes the "Captured frames" popup of an exposure row (the value is already bound).</summary>
        private void ProgressOk_Click(object sender, RoutedEventArgs e) {
            DependencyObject d = sender as DependencyObject;
            while (d != null && d is not System.Windows.Controls.Primitives.Popup) {
                d = System.Windows.Media.VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
            }
            if (d is System.Windows.Controls.Primitives.Popup popup) {
                popup.IsOpen = false;
                if (popup.PlacementTarget is System.Windows.Controls.Primitives.ToggleButton toggle) { toggle.IsChecked = false; }
            }
        }
    }
}
