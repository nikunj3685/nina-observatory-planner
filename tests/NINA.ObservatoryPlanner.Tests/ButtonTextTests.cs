using FluentAssertions;
using NINA.ObservatoryPlanner.UI;
using NUnit.Framework;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NINA.ObservatoryPlanner.Tests {

    /// <summary>NINA's theme sets an implicit (light) TextBlock style; the planner's buttons must keep their own text colour.</summary>
    [TestFixture, Apartment(ApartmentState.STA), NonParallelizable]
    public class ButtonTextTests {

        private static T Find<T>(DependencyObject d) where T : DependencyObject {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) {
                var c = VisualTreeHelper.GetChild(d, i);
                if (c is T t) { return t; }
                var r = Find<T>(c); if (r != null) { return r; }
            }
            return null;
        }

        [Test]
        public void The_primary_button_text_is_dark_on_orange_even_with_a_light_theme_text_style() {
            var view = new PlannerView();
            var button = new Button { Content = "▶ Run forever", Style = (Style)view["OP_Primary"] };
            var w = new Window { Content = button, Width = 300, Height = 100, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000 };
            // like NINA's theme: the application's resources make every TextBlock light (only those reach inside templates)
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources[typeof(TextBlock)] = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.ForegroundProperty, Brushes.White) } };
            w.Show();
            w.UpdateLayout();
            var text = Find<TextBlock>(button);
            text.Should().NotBeNull();
            ((SolidColorBrush)text.Foreground).Color.Should().Be(((SolidColorBrush)view["OP_AccentInk"]).Color);
            w.Close();
            app.Resources.Remove(typeof(TextBlock));
        }
    }
}
