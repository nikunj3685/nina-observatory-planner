using FluentAssertions;
using NINA.ObservatoryPlanner.UI;
using NUnit.Framework;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace NINA.ObservatoryPlanner.Tests {

    /// <summary>The page must scroll with the mouse over a stage list once the list has nothing left to scroll that way.</summary>
    [TestFixture, Apartment(ApartmentState.STA), NonParallelizable]
    public class WheelBubblingTests {
        private Window window;
        private ScrollViewer page;
        private ScrollViewer list;

        [SetUp]
        public void SetUp() {
            // a 300 px page holding a spacer and a stage list that shows 100 of its 400 px
            list = new ScrollViewer { MaxHeight = 100, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new Border { Height = 400 } };
            WheelBubbling.SetAtEnds(list, true);
            page = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new StackPanel { Children = { new Border { Height = 600 }, list, new Border { Height = 600 } } } };
            window = new Window { Content = page, Width = 300, Height = 300, ShowActivated = false, Left = -10000, WindowStartupLocation = WindowStartupLocation.Manual };
            window.Show();
            page.ScrollToVerticalOffset(500);
            window.UpdateLayout();
        }

        [TearDown]
        public void TearDown() => window.Close();

        private void Wheel(int delta) {
            list.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
            window.UpdateLayout();
        }

        [Test]
        public void Wheel_up_over_a_list_already_at_its_top_scrolls_the_page() {
            list.VerticalOffset.Should().Be(0);
            Wheel(120);
            page.VerticalOffset.Should().BeLessThan(500, "the list cannot go further up, so the page scrolls");
            list.VerticalOffset.Should().Be(0);
        }

        [Test]
        public void Wheel_down_over_a_list_at_its_bottom_scrolls_the_page() {
            list.ScrollToBottom();
            window.UpdateLayout();
            Wheel(-120);
            page.VerticalOffset.Should().BeGreaterThan(500);
        }

        [Test]
        public void The_list_keeps_the_wheel_while_it_can_still_scroll_that_way() {
            WheelBubbling.CanScroll(list, -120).Should().BeTrue("the list is at its top and can scroll down");
            WheelBubbling.CanScroll(list, 120).Should().BeFalse();
            Wheel(-120);
            page.VerticalOffset.Should().Be(500, "the page does not move while the list scrolls");
        }

        [Test]
        public void A_list_with_nothing_to_scroll_passes_the_wheel_on() {
            list.Content = new Border { Height = 40 };
            window.UpdateLayout();
            WheelBubbling.CanScroll(list, 120).Should().BeFalse();
            WheelBubbling.CanScroll(list, -120).Should().BeFalse();
            Wheel(-120);
            page.VerticalOffset.Should().BeGreaterThan(500);
        }
    }
}
