using FluentAssertions;
using Moq;
using NINA.ObservatoryPlanner.Core;
using NINA.ObservatoryPlanner.Nina;
using NINA.ObservatoryPlanner.UI;
using NINA.Profile.Interfaces;
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace NINA.ObservatoryPlanner.Tests {

    /// <summary>The panel rearranges by width: buttons into menus, exposure rows onto more lines, list beside or above the details.</summary>
    [TestFixture, Apartment(ApartmentState.STA), NonParallelizable]
    public class LayoutTests {

        private static Border Box(double width, double height = 20) => new() { Width = width, Height = height };

        private static void Layout(UIElement e, double width, double height = double.PositiveInfinity) {
            e.Measure(new Size(width, height));
            e.Arrange(new Rect(0, 0, width, double.IsInfinity(height) ? e.DesiredSize.Height : height));
        }

        [Test]
        public void First_fit_shows_the_first_child_that_fits_and_falls_back_to_the_last() {
            var wide = Box(300);
            var narrow = Box(120);
            var icon = Box(40);
            var panel = new FirstFitPanel { Children = { wide, narrow, icon } };

            Layout(panel, 400);
            panel.ChosenIndex.Should().Be(0);
            wide.Visibility.Should().Be(Visibility.Visible);
            narrow.Visibility.Should().Be(Visibility.Hidden);

            Layout(panel, 200);
            panel.ChosenIndex.Should().Be(1, "the buttons do not fit, the menu does");

            Layout(panel, 30);
            panel.ChosenIndex.Should().Be(2, "nothing fits: the last child is the fallback");
            icon.Visibility.Should().Be(Visibility.Visible);
        }

        [Test]
        public void Exposure_row_stays_on_one_line_while_it_fits_then_moves_the_progress_bar_under_it() {
            var filter = ExposureRowPanel.FilterWidth(5);
            double Sum(params double[] w) => w.Sum() + (w.Length - 1) * ExposureRowPanel.Gap;
            var oneLine = Sum(ExposureRowPanel.PriorityWidth, ExposureRowPanel.RunWidth, ExposureRowPanel.TypeWidth, filter, ExposureRowPanel.NumberWidth,
                ExposureRowPanel.BinningWidth, ExposureRowPanel.NumberWidth, ExposureRowPanel.ProgressWidth, ExposureRowPanel.DeleteWidth);

            ExposureRowPanel.ModeFor(oneLine + 50, filter).Should().Be(ExposureRowMode.OneLine);
            ExposureRowPanel.ModeFor(oneLine, filter).Should().Be(ExposureRowMode.OneLine);
            ExposureRowPanel.ModeFor(oneLine - ExposureRowPanel.ProgressWidth, filter).Should().Be(ExposureRowMode.ProgressBelow,
                "without room for the progress bar the fields still fit on one line");
            ExposureRowPanel.ModeFor(380, filter).Should().Be(ExposureRowMode.TwoLines);
            ExposureRowPanel.ModeFor(250, filter).Should().Be(ExposureRowMode.ThreeLines);
            ExposureRowPanel.ModeFor(double.PositiveInfinity, filter).Should().Be(ExposureRowMode.OneLine);
        }

        [Test]
        public void Exposure_row_lays_out_its_cells_and_marks_a_progress_bar_on_its_own_line() {
            var cells = Enumerable.Range(0, 9).Select(_ => Box(10)).ToArray();
            var row = new ExposureRowPanel();
            foreach (var c in cells) { row.Children.Add(c); }

            Layout(row, 1000);
            row.Mode.Should().Be(ExposureRowMode.OneLine);
            row.DesiredSize.Height.Should().Be(20, "one line");
            ExposureRowPanel.GetProgressOnOwnLine(cells[7]).Should().BeFalse();

            Layout(row, 560);
            row.Mode.Should().Be(ExposureRowMode.ProgressBelow);
            row.DesiredSize.Height.Should().Be(20 + ExposureRowPanel.LineGap + 20, "the progress bar has a line of its own");
            ExposureRowPanel.GetProgressOnOwnLine(cells[7]).Should().BeTrue();
            LayoutInformation.GetLayoutSlot(cells[7]).Width.Should().BeGreaterThan(300, "under the row the progress bar spans from Type to Delete");

            Layout(row, 250);
            row.Mode.Should().Be(ExposureRowMode.ThreeLines);
            ExposureRowPanel.GetProgressOnOwnLine(cells[7]).Should().BeFalse("in the narrowest layout it sits next to Repeat");
        }

        [Test]
        public void Exposure_header_leaves_out_the_progress_label_once_the_bar_is_under_the_row() {
            var cells = Enumerable.Range(0, 9).Select(_ => Box(10)).ToArray();
            var header = new ExposureRowPanel { IsHeader = true };
            foreach (var c in cells) { header.Children.Add(c); }

            Layout(header, 1000);
            cells[7].Visibility.Should().Be(Visibility.Visible);
            Layout(header, 560);
            cells[7].Visibility.Should().Be(Visibility.Hidden);
            header.DesiredSize.Height.Should().Be(20, "the header keeps one line");
        }

        [Test]
        public void Wider_filter_names_need_a_wider_filter_column() {
            ExposureRowPanel.FilterWidth(13).Should().BeGreaterThan(ExposureRowPanel.FilterWidth(5));
        }

        [Test]
        public void List_and_details_sit_side_by_side_with_the_same_height_or_stack() {
            var list = Box(double.NaN, 400);
            list.Width = double.NaN;
            var details = new Border { Height = 300 };
            var panel = new SideBySidePanel { ListWidth = 270, DetailsMinWidth = 430, Children = { new Border { Child = list }, details } };

            Layout(panel, 900);
            panel.Side.Should().BeTrue();
            SideBySidePanel.GetIsSideBySide(panel.Children[0]).Should().BeTrue();
            panel.DesiredSize.Height.Should().Be(300, "side by side the list is as tall as the details");

            Layout(panel, 500);
            panel.Side.Should().BeFalse();
            panel.DesiredSize.Height.Should().Be(400 + 10 + 300, "stacked: the list above the details");
        }

        [Test]
        public void A_list_shows_four_rows_then_scrolls() {
            var items = new ItemsControl { ItemsSource = Enumerable.Range(1, 7).ToList(), ItemTemplate = null };
            items.ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(Border)) };
            items.ItemTemplate.VisualTree.SetValue(FrameworkElement.HeightProperty, 25.0);
            var scroller = new CappedScroller { MaxItems = 4, Child = new ScrollViewer { Content = items, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
            var w = new Window { Content = scroller, Width = 300, Height = 800, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, SizeToContent = SizeToContent.Height };
            w.Show();
            w.UpdateLayout();
            scroller.ActualHeight.Should().BeApproximately(100, 1, "four rows of 25");
            w.Close();
        }

        [Test]
        public void The_info_guide_pictures_are_in_the_plugin() {
            _ = System.IO.Packaging.PackUriHelper.UriSchemePack; // registers pack:// outside a WPF application
            foreach (var name in new[] { "info-top.png", "info-targets.png", "info-exposures.png", "info-stage.png" }) {
                var info = Application.GetResourceStream(new Uri($"pack://application:,,,/NINA.ObservatoryPlanner;component/UI/Images/{name}"));
                info.Should().NotBeNull(name);
                info.Stream.Length.Should().BeGreaterThan(1000);
            }
        }

        [Test]
        public void Columns_follow_the_width() {
            AutoColumnsPanel.ColumnsFor(300, 4, 330, 10, 2).Should().Be(1);
            AutoColumnsPanel.ColumnsFor(700, 4, 330, 10, 2).Should().Be(2);
            AutoColumnsPanel.ColumnsFor(2000, 4, 330, 10, 2).Should().Be(2, "at most two stage columns");
            AutoColumnsPanel.ColumnsFor(2000, 11, 140, 10, 3).Should().Be(3);
        }

        [Test]
        public void A_cut_off_name_gets_its_full_text_as_a_tooltip() {
            var text = new TextBlock { Text = "M 33 Triangulum Galaxy with NGC 604 region", TextTrimming = TextTrimming.CharacterEllipsis };
            TrimTip.SetEnabled(text, true);
            var w = new Window { Content = new Grid { Width = 80, Children = { text } }, Width = 300, Height = 100, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000 };
            w.Show();
            w.UpdateLayout();
            text.ToolTip.Should().Be(text.Text);
            ((Grid)w.Content).Width = 280;
            w.UpdateLayout();
            text.ToolTip.Should().BeNull("a name that fits has no tooltip");
            w.Close();
        }

        [Test]
        public void Up_and_down_buttons_stay_within_the_allowed_numbers() {
            var row = new PlannerExposure { ExposureTime = 9999, Count = 1, Done = 0 };
            PlannerView.Step(row, "exposure", 1);
            row.ExposureTime.Should().Be(9999);
            PlannerView.Step(row, "exposure", -1);
            row.ExposureTime.Should().Be(9998);
            PlannerView.Step(row, "repeat", -1);
            row.Count.Should().Be(1, "at least one frame");
            PlannerView.Step(row, "done", -1);
            row.Done.Should().Be(0);
            PlannerView.Step(row, "done", 1);
            row.Done.Should().Be(1);
        }

        [Test]
        public void Text_size_is_kept_between_10_and_18_in_half_steps() {
            var o = new PlannerOptions();
            o.PanelTextSize.Should().Be(10);
            o.PanelTextSize = 25;
            o.PanelTextSize.Should().Be(18);
            o.PanelTextSize = 12.3;
            o.PanelTextSize.Should().Be(12.5);
            var copy = new PlannerOptions();
            copy.CopyFrom(o);
            copy.PanelTextSize.Should().Be(12.5, "the text size is saved with the other options");
        }

        [Test]
        public void Panel_view_model_gives_the_layout_what_it_needs() {
            var root = Path.Combine(Path.GetTempPath(), "op-layout-" + Guid.NewGuid().ToString("N"));
            try {
                var planner = new PlannerService(null, root);
                var t = planner.NewTarget("Barnard 175");
                t.Exposures.Clear();
                t.Exposures.Add(new PlannerExposure { Filter = "Ha 3nm Astrodon" });
                planner.Targets.Add(t);
                var vm = new PlannerDockableVM(new Mock<IProfileService>().Object, planner);
                vm.SelectedTarget = t;

                vm.FilterColumnChars.Should().Be(13, "the longest name counts up to 12 characters, plus one");
                t.Exposures[0].Filter = "Ha";
                vm.FilterColumnChars.Should().Be(3, "\"L\" from the empty filter wheel and \"Ha\": two characters plus one");

                vm.PanelScale.Should().BeApproximately(10 / 12.0, 1e-9);
                planner.Options.PanelTextSize = 18;
                vm.PanelScale.Should().Be(1.5);

                vm.CurrentStageText.Should().BeEmpty("nothing runs");
                vm.Stages[2].CountText.Should().Be("0 triggers");
                vm.Stages[0].CountText.Should().Be("0 steps");

                vm.SelectedPlannerTab = 3;
                planner.Options.RunMode = RunMode.WithoutSafety;
                vm.TabHasWarnings.Should().BeTrue("Info shows the no-safety warning");
                vm.DismissWarningCommand.Execute("no-safety");
                vm.TabHasWarnings.Should().BeFalse("closing the warning removes the ⚠");
                vm.SelectedPlannerTab = 2;
                vm.TabHasWarnings.Should().BeFalse("Options has no warnings");
            } finally {
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }
    }
}
