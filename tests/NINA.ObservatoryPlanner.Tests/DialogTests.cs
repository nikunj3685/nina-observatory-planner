using FluentAssertions;
using Moq;
using NINA.ObservatoryPlanner.Core;
using NINA.ObservatoryPlanner.Nina;
using NINA.ObservatoryPlanner.UI;
using NINA.ObservatoryPlanner.UI.Dialogs;
using NINA.Profile.Interfaces;
using NUnit.Framework;
using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace NINA.ObservatoryPlanner.Tests {

    /// <summary>
    /// Builds the panel and the dialogs for real (compiling XAML misses runtime resource and binding errors).
    /// No NINA here: the services are absent and NINA's theme resources are missing, which the UI must tolerate.
    /// </summary>
    [TestFixture, Apartment(ApartmentState.STA), NonParallelizable]
    public class DialogTests {
        private string root;
        private PlannerService planner;

        [SetUp]
        public void SetUp() {
            root = Path.Combine(Path.GetTempPath(), "op-dialogs-" + Guid.NewGuid().ToString("N"));
            planner = new PlannerService(null, root);
            // NGC 7000: high in the evening at 45.5° N in October, sets through 30° after midnight
            var t = planner.NewTarget("NGC 7000");
            t.RaHours = 20.98;
            t.DecDegrees = 44.3;
            t.End = new TimeConstraint { Enabled = true, By = ConstraintBy.Altitude, Altitude = 30 };
            planner.Targets.Add(t);
        }

        private static NightPlan Tonight() => NightPlan.For(TestData.Site, TestData.Night(15), TestData.Utc);

        [TearDown]
        public void TearDown() {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }

        private static void ShowAndClose(Window w) {
            w.ShowActivated = false;
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = -10000;
            w.Show();
            w.UpdateLayout();
            w.ActualWidth.Should().BeGreaterThan(0);
            w.Close();
        }

        [Test]
        public void Target_settings_and_planning_tools_open() {
            var vm = new TargetSettingsVM(planner, planner.Targets[0], isNew: false, Tonight());
            vm.End.TimeText.Should().NotBeNullOrEmpty("the end time is calculated for tonight from the 30° altitude");
            ShowAndClose(new TargetSettingsWindow(vm));
            ShowAndClose(new PlanningToolsWindow(vm));
        }

        [Test]
        public void Target_settings_apply_writes_back_and_rejects_bad_coordinates() {
            var t = planner.Targets[0];
            var vm = new TargetSettingsVM(planner, t, isNew: false, Tonight()) { Name = "Orion Nebula", RaText = "05h35m17.3s", DecText = "-05°23'28\"" };
            vm.Start.Enabled = true;
            vm.Start.ByAltitude = false;
            vm.Start.TimeText = "23:40";
            vm.Apply().Should().BeNull();
            t.Name.Should().Be("Orion Nebula");
            t.RaHours.Should().BeApproximately(5 + 35 / 60.0 + 17.3 / 3600, 1e-6);
            t.Start.Should().BeEquivalentTo(new { Enabled = true, By = ConstraintBy.Time, Time = new TimeSpan(23, 40, 0) });

            vm.RaText = "not a coordinate";
            vm.Apply().Should().NotBeNull();
            t.Name.Should().Be("Orion Nebula", "nothing is written when the input is invalid");
        }

        [Test]
        public void Slew_warns_about_a_collision_below_the_horizon_or_low_down_only() {
            // without NINA the site is 0° N 0° E: a target on the local meridian at Dec 0 is overhead
            var utc = new DateTime(2026, 10, 2, 3, 0, 0, DateTimeKind.Utc);
            var lst = Sky.GmstDegrees(utc) / 15;
            var vm = new TargetSettingsVM(planner, planner.Targets[0], isNew: false, Tonight());
            vm.CollisionWarning(lst, 0, utc).Should().BeNull("overhead is safe");
            vm.CollisionWarning(lst, 85, utc).Should().Contain("very low").And.Contain("Slew anyway");
            vm.CollisionWarning((lst + 12) % 24, 0, utc).Should().Contain("below the horizon");
        }

        [Test]
        public void Planning_chart_click_sets_the_start_time_and_altitude() {
            var vm = new TargetSettingsVM(planner, planner.Targets[0], isNew: false, Tonight());
            var at = vm.Night.NightStart.AddHours(13).AddMinutes(30); // 01:30
            vm.Start.SetTime(at);
            vm.Start.Enabled.Should().BeTrue();
            vm.Start.ByAltitude.Should().BeFalse();
            vm.Start.TimeText.Should().Be(ConstraintRow.FormatTime(new TimeSpan(1, 30, 0)));
            vm.Preview().Start.Altitude.Should().BeApproximately(vm.Night.Altitude(20.98, 44.3, at), 0.06);
        }

        [Test]
        public void Ask_and_library_windows_open() {
            ShowAndClose(new AskWindow { Heading = "Delete target", Message = "Delete \"NGC 7000\"?", CheckText = "Don't ask me again", Buttons = new[] { Ask.Danger("Delete"), Ask.Cancel() } });
            ShowAndClose(new AskWindow {
                Heading = "Load a default workflow", Message = "Pick one",
                Choices = new[] { new AskChoice { Name = "Safety and dome", Description = "Unattended", IsSelected = true } }, Buttons = new[] { Ask.Primary("Load"), Ask.Cancel() }
            });
            ShowAndClose(new AskWindow { Heading = "Save as", TextLabel = "Name", Buttons = new[] { Ask.Primary("Save"), Ask.Cancel() } });
            ShowAndClose(new LibraryWindow("Open target list", "target lists", planner.Store.TargetsFolder, null, null));
        }

        [Test]
        public void Warning_messages_can_be_closed() {
            var vm = new PlannerDockableVM(new Mock<IProfileService>().Object, planner);
            planner.SetEndProblems("4 End: 1 step failed: Warm Camera.");
            vm.EndProblems.Should().NotBeNull();
            vm.DismissEndProblemsCommand.Execute(null);
            vm.EndProblems.Should().BeNull();

            planner.Options.RunMode = RunMode.WithoutSafety;
            vm.NoSafetyWarningShown.Should().BeTrue();
            vm.DismissWarningCommand.Execute("no-safety");
            vm.NoSafetyWarningShown.Should().BeFalse();
        }

        [Test]
        public void Panel_template_renders_with_the_view_model() {
            var profile = new Mock<IProfileService>();
            var vm = new PlannerDockableVM(profile.Object, planner);
            var template = (DataTemplate)new PlannerView()["NINA.ObservatoryPlanner.UI.PlannerDockableVM_Dockable"];
            var w = new Window { Content = new ContentControl { Content = vm, ContentTemplate = template }, Width = 1200, Height = 900 };
            vm.SelectedTarget.Should().NotBeNull();
            vm.TargetChips.Should().Contain("ends at 30° altitude");
            vm.NextTarget.Should().BeSameAs(planner.Targets[0]);
            foreach (var tab in new[] { 0, 1, 2, 3 }) {
                vm.SelectedPlannerTab = tab;
                ShowAndClose(w);
                w = new Window { Content = new ContentControl { Content = vm, ContentTemplate = template }, Width = 1200, Height = 900 };
            }
            vm.GoInfoCommand.Execute(null);
            vm.SelectedPlannerTab.Should().Be(3, "the ! next to the gear opens Info");
            vm.Stages.Should().OnlyContain(s => s.EmptyText != null, "no workflow is loaded without NINA");
        }
    }
}
