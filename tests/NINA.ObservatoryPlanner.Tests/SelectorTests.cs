using FluentAssertions;
using NINA.ObservatoryPlanner.Core;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace NINA.ObservatoryPlanner.Tests {

    [TestFixture]
    public class SelectorTests {

        /// <summary>The user's night: target 1 from dusk to 01:00, target 2 from 01:05 to 04:00, still safe after 04:00.</summary>
        private static (TargetSelector s, List<PlannerTarget> list, PlannerTarget t1, PlannerTarget t2) UserNight() {
            var t1 = TestData.Circumpolar("Target 1", frames: 100);
            t1.End = TestData.At(1, 0);
            var t2 = TestData.Circumpolar("Target 2", frames: 100);
            t2.Start = TestData.At(1, 5);
            t2.End = TestData.At(4, 0);
            return (TestData.Selector(), new List<PlannerTarget> { t1, t2 }, t1, t2);
        }

        [Test]
        public void Evening_runs_target_1() {
            var (s, list, t1, _) = UserNight();
            var d = s.Decide(list, TestData.Night(22));
            d.Kind.Should().Be(DecisionKind.RunNow);
            d.Target.Should().BeSameAs(t1);
        }

        [Test]
        public void A_frame_that_would_end_after_01_00_is_not_started() {
            var (s, _, t1, _) = UserNight();
            s.CanStartFrame(t1, TestData.Night(0, 50), TimeSpan.FromSeconds(300)).Should().BeTrue();
            s.CanStartFrame(t1, TestData.Night(0, 57), TimeSpan.FromSeconds(300)).Should().BeFalse();
        }

        [Test]
        public void Between_01_00_and_01_05_it_waits_for_target_2() {
            var (s, list, _, t2) = UserNight();
            var d = s.Decide(list, TestData.Night(1, 0).AddSeconds(30));
            d.Kind.Should().Be(DecisionKind.WaitUntil);
            d.Target.Should().BeSameAs(t2);
            d.At.Should().BeCloseTo(TestData.Night(1, 5), TimeSpan.FromMinutes(1));
        }

        [Test]
        public void At_01_06_it_runs_target_2() {
            var (s, list, _, t2) = UserNight();
            s.Decide(list, TestData.Night(1, 6)).Target.Should().BeSameAs(t2);
        }

        [Test]
        public void After_04_00_there_is_nothing_left_tonight() {
            var (s, list, _, _) = UserNight();
            s.Decide(list, TestData.Night(4, 1)).Kind.Should().Be(DecisionKind.NothingTonight);
        }

        [Test]
        public void List_order_is_priority() {
            var (s, _, t1, t2) = UserNight();
            t2.Start = new TimeConstraint();
            s.Decide(new List<PlannerTarget> { t2, t1 }, TestData.Night(22)).Target.Should().BeSameAs(t2);
        }

        [Test]
        public void Unchecked_complete_or_skipped_targets_are_ignored() {
            var (s, list, t1, t2) = UserNight();
            t2.Start = new TimeConstraint();
            t1.Enabled = false;
            s.Decide(list, TestData.Night(22)).Target.Should().BeSameAs(t2);
            t1.Enabled = true;
            t1.Exposures[0].Done = t1.Exposures[0].Count;
            s.Decide(list, TestData.Night(22)).Target.Should().BeSameAs(t2);
            s.Decide(list, TestData.Night(22), new HashSet<Guid> { t2.Id }).Kind.Should().NotBe(DecisionKind.RunNow);
        }

        [Test]
        public void Nothing_is_imaged_in_daylight() {
            var (s, list, _, _) = UserNight();
            s.IsDark(TestData.Night(15)).Should().BeFalse();
            s.Decide(list, TestData.Night(15)).Kind.Should().Be(DecisionKind.WaitUntil, "target 1 opens when it gets dark");
        }

        [Test]
        public void Start_at_altitude_waits_until_the_target_has_risen() {
            // M42 (RA 5.59 h, Dec −5.39°) rises late on an October night at this site.
            var m42 = new PlannerTarget {
                Name = "M42", RaHours = 5.588, DecDegrees = -5.39,
                Start = new TimeConstraint { Enabled = true, By = ConstraintBy.Altitude, Altitude = 30 },
                Exposures = new ObservableCollection<PlannerExposure> { new PlannerExposure { Count = 10 } }
            };
            var s = TestData.Selector();
            s.IsOpenAt(m42, TestData.Night(22)).Should().BeFalse();
            var d = s.Decide(new List<PlannerTarget> { m42 }, TestData.Night(22));
            d.Kind.Should().Be(DecisionKind.WaitUntil);
            s.Altitude(m42, d.At).Should().BeApproximately(30, 0.5);
        }

        [Test]
        public void End_at_altitude_stops_when_the_target_sets_below_it() {
            var t = TestData.Circumpolar("IC1396");
            t.RaHours = 21.672; t.DecDegrees = 57.567;
            t.End = new TimeConstraint { Enabled = true, By = ConstraintBy.Altitude, Altitude = 39 };
            var s = TestData.Selector();
            s.IsOpenAt(t, TestData.Night(22)).Should().BeTrue("high in the sky at 22:00");
            s.IsOpenAt(t, TestData.Night(5)).Should().BeFalse("below 39° and setting before dawn");
        }
    }

    [TestFixture]
    public class ExposureOrderTests {

        private static PlannerTarget ThreeFilters(ExposureOrder order) => new() {
            Order = order,
            Exposures = new ObservableCollection<PlannerExposure> {
                new PlannerExposure { Filter = "Ha", Count = 2 }, new PlannerExposure { Filter = "OIII", Count = 1 }, new PlannerExposure { Filter = "SII", Count = 2 }
            }
        };

        private static List<string> Shoot(PlannerTarget t) {
            var shot = new List<string>();
            PlannerExposure last = null;
            while (ExposurePlanner.Next(t, last) is { } e) { e.Done++; shot.Add(e.Filter); last = e; }
            return shot;
        }

        [Test]
        public void Finish_each_row_first() =>
            Shoot(ThreeFilters(ExposureOrder.FinishEachRowFirst)).Should().Equal("Ha", "Ha", "OIII", "SII", "SII");

        [Test]
        public void Rotate_through_filters_skips_rows_that_are_done() =>
            Shoot(ThreeFilters(ExposureOrder.RotateThroughFilters)).Should().Equal("Ha", "OIII", "SII", "Ha", "SII");

        [Test]
        public void Unchecked_rows_are_not_shot() {
            var t = ThreeFilters(ExposureOrder.FinishEachRowFirst);
            t.Exposures[1].Enabled = false;
            Shoot(t).Should().Equal("Ha", "Ha", "SII", "SII");
        }

        [Test]
        public void Progress_and_remaining_time() {
            var t = ThreeFilters(ExposureOrder.FinishEachRowFirst);
            t.DelayBetween = 10;
            t.Exposures[0].Done = 1;
            t.TotalFrames.Should().Be(5);
            t.DoneFrames.Should().Be(1);
            t.Percent.Should().Be(20);
            t.HoursRemaining.Should().BeApproximately(4 * (120 + 10) / 3600.0, 1e-9);
        }
    }

    [TestFixture]
    public class GapPlanTests {

        [Test]
        public void Keep_tracking_only_stops_guiding() {
            var (wait, resume) = GapPlan.For(GapMountAction.KeepTracking, closeDome: true);
            wait.Should().Equal(GapStep.StopGuiding);
            resume.Should().BeEmpty();
        }

        [Test]
        public void Stop_tracking_and_park() {
            var (wait, resume) = GapPlan.For(GapMountAction.StopTrackingAndPark, closeDome: false);
            wait.Should().Equal(GapStep.StopGuiding, GapStep.StopTracking, GapStep.Park);
            resume.Should().Equal(GapStep.Unpark);
        }

        [Test]
        public void Stop_tracking_and_park_with_dome() {
            var (wait, resume) = GapPlan.For(GapMountAction.StopTrackingAndPark, closeDome: true);
            wait.Should().Equal(GapStep.StopGuiding, GapStep.StopTracking, GapStep.Park, GapStep.CloseDome);
            resume.Should().Equal(GapStep.OpenDome, GapStep.Unpark);
        }

        [Test]
        public void Find_home_never_closes_the_dome() {
            var (wait, resume) = GapPlan.For(GapMountAction.StopTrackingAndFindHome, closeDome: true);
            wait.Should().Equal(GapStep.StopGuiding, GapStep.StopTracking, GapStep.FindHome);
            resume.Should().BeEmpty();
        }
    }
}
