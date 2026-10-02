using FluentAssertions;
using NINA.ObservatoryPlanner.UI;
using NUnit.Framework;
using System.Threading;
using System.Windows;

namespace NINA.ObservatoryPlanner.Tests {

    /// <summary>Compiling XAML does not catch runtime template errors; build the panel template for real.</summary>
    [TestFixture, Apartment(ApartmentState.STA), NonParallelizable]
    public class PanelTemplateTests {

        [Test]
        public void Dockable_template_is_found_by_NINAs_key_and_loads() {
            var view = new PlannerView();
            view.Contains("NINA.ObservatoryPlanner.UI.PlannerDockableVM_Dockable").Should().BeTrue("NINA looks the template up by '<VM type>_Dockable'");
            var template = (DataTemplate)view["NINA.ObservatoryPlanner.UI.PlannerDockableVM_Dockable"];
            var content = template.LoadContent();
            content.Should().NotBeNull();
        }

        [Test]
        public void Coordinates_round_trip() {
            CoordinateText.TryParseRa(CoordinateText.FormatRa(21.6720), out var ra).Should().BeTrue();
            ra.Should().BeApproximately(21.6720, 1e-4);
            CoordinateText.TryParseDec(CoordinateText.FormatDec(-5.39), out var dec).Should().BeTrue();
            dec.Should().BeApproximately(-5.39, 1e-4);
            CoordinateText.TryParseRa("21h40m19.35s", out ra).Should().BeTrue();
            ra.Should().BeApproximately(21 + 40 / 60.0 + 19.35 / 3600, 1e-6);
            CoordinateText.TryParseDec("+57°34'00.4\"", out dec).Should().BeTrue();
            dec.Should().BeApproximately(57 + 34 / 60.0 + 0.4 / 3600, 1e-6);
            CoordinateText.TryParseDec("-10 27 0", out dec).Should().BeTrue();
            dec.Should().BeApproximately(-10.45, 1e-6);
        }
    }
}
