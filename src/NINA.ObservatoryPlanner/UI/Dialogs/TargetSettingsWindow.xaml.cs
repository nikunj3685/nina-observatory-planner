using System.Windows;

namespace NINA.ObservatoryPlanner.UI.Dialogs {

    public partial class TargetSettingsWindow : Window {
        private readonly TargetSettingsVM vm;

        public TargetSettingsWindow(TargetSettingsVM vm) {
            InitializeComponent();
            this.vm = vm;
            DataContext = vm;
            constraints.ItemsSource = new[] { vm.Start, vm.End };
            Owner = Application.Current?.MainWindow is { IsLoaded: true } main ? main : null;
            if (Owner == null) { WindowStartupLocation = WindowStartupLocation.CenterScreen; }
            Loaded += (_, _) => { nameBox.Focus(); if (vm.IsNew) { nameBox.SelectAll(); } };
        }

        private void Ok_Click(object sender, RoutedEventArgs e) {
            // commit the text box that has focus before reading the values
            (Keyboard_FocusedTextBox() as System.Windows.Controls.TextBox)?.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
            var problem = vm.Apply();
            if (problem != null) {
                error.Text = problem;
                return;
            }
            DialogResult = true;
        }

        private static IInputElement Keyboard_FocusedTextBox() => System.Windows.Input.Keyboard.FocusedElement;

        private void Planning_Click(object sender, RoutedEventArgs e) {
            new PlanningToolsWindow(vm) { Owner = this }.ShowDialog();
        }
    }
}
