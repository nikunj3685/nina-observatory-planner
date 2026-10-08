using FluentAssertions;
using Moq;
using NINA.Equipment.Interfaces.Mediator;
using NINA.ObservatoryPlanner.Core;
using NINA.ObservatoryPlanner.Nina;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem.Imaging;
using NINA.Sequencer.SequenceItem.Utility;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using NUnit.Framework;
using System;
using System.IO;

namespace NINA.ObservatoryPlanner.Tests {

    /// <summary>Autofocus before the first light frame after 1 Begin, and "AF after filter change (planner)".</summary>
    [TestFixture]
    public class AutofocusRuleTests {
        private string root;
        private PlannerService planner;
        private PlannerTarget target;

        [SetUp]
        public void SetUp() {
            root = Path.Combine(Path.GetTempPath(), "op-af-" + Guid.NewGuid().ToString("N"));
            planner = new PlannerService(null, root);
            target = planner.NewTarget("M31");
        }

        [TearDown]
        public void TearDown() { try { Directory.Delete(root, true); } catch (IOException) { } }

        private static PlannerExposure Row(string filter, ExposureType type = ExposureType.Light) => new() { Filter = filter, Type = type };

        /// <summary>What the imaging loop does for one frame: returns whether the after-Begin autofocus ran and whether the filter trigger would.</summary>
        private (bool afterBegin, bool filterChange) Frame(PlannerExposure e) {
            planner.PrepareFrame(e);
            var afterBegin = planner.TakeFocusAfterBegin(e);
            if (afterBegin) { planner.MarkFocused(); }
            var filterChange = planner.FilterChangeNeedsFocus();
            planner.OnFrameStarting(target, e);
            return (afterBegin, filterChange);
        }

        [Test]
        public void First_light_frame_after_1_Begin_is_focused_once() {
            planner.OnBeginStarting();
            Frame(Row("L")).Should().Be((true, false));
            Frame(Row("L")).Should().Be((false, false), "only the first frame after 1 Begin");
        }

        [Test]
        public void Darks_before_the_first_light_frame_keep_the_autofocus_for_it() {
            planner.OnBeginStarting();
            Frame(Row("L", ExposureType.Dark)).afterBegin.Should().BeFalse("darks and bias frames don't need focus");
            Frame(Row("L", ExposureType.Bias)).afterBegin.Should().BeFalse();
            Frame(Row("L")).afterBegin.Should().BeTrue();
        }

        [Test]
        public void The_option_turns_the_autofocus_after_1_Begin_off() {
            planner.Options.AutofocusAfterBegin = false;
            planner.OnBeginStarting();
            Frame(Row("L")).afterBegin.Should().BeFalse();
        }

        [Test]
        public void A_filter_change_between_light_frames_focuses_and_the_same_filter_does_not() {
            Frame(Row("Ha")).Should().Be((false, false), "the first frame of the session has nothing to compare with");
            Frame(Row("Ha")).filterChange.Should().BeFalse();
            Frame(Row("OIII")).filterChange.Should().BeTrue();
            Frame(Row("oiii")).filterChange.Should().BeFalse("filter names are compared without case");
        }

        [Test]
        public void Dark_and_flat_frames_are_ignored_by_the_filter_change_rule() {
            Frame(Row("Ha"));
            Frame(Row("OIII", ExposureType.Flat)).filterChange.Should().BeFalse("a flat is not a light frame");
            Frame(Row("Ha")).filterChange.Should().BeFalse("compared with the previous light frame (Ha), not the flat");
        }

        [Test]
        public void The_filter_change_survives_a_new_target_or_a_pause() {
            Frame(Row("Ha"));
            planner.OnImagingEnded(target);   // the target ends, or the run pauses
            var next = planner.NewTarget("M33");
            planner.PrepareFrame(Row("OIII"));
            planner.FilterChangeNeedsFocus().Should().BeTrue("the last light frame was Ha, on the previous target");
            next.Should().NotBeNull();
        }

        [Test]
        public void After_1_Begin_with_a_new_filter_autofocus_runs_once_not_twice() {
            Frame(Row("Ha"));
            planner.OnEndStarting();
            planner.OnBeginStarting();
            Frame(Row("OIII")).Should().Be((true, false), "the autofocus after 1 Begin already focused on OIII");
        }

        [Test]
        public void The_trigger_fires_only_before_a_take_exposure() {
            static T M<T>() where T : class => new Mock<T> { DefaultValue = DefaultValue.Mock }.Object;
            var trigger = new PlannerAutofocusOnFilterChange(planner);
            Frame(Row("Ha"));
            planner.PrepareFrame(Row("OIII"));
            var take = new TakeExposure(M<IProfileService>(), M<ICameraMediator>(), M<IImagingMediator>(), M<IImageSaveMediator>(), M<IImageHistoryVM>());
            trigger.ShouldTrigger(null, take).Should().BeTrue();
            trigger.ShouldTrigger(null, new WaitForTimeSpan()).Should().BeFalse();
            planner.MarkFocused();
            trigger.ShouldTrigger(null, take).Should().BeFalse("autofocus already ran for this frame");
            ((PlannerAutofocusOnFilterChange)trigger.Clone()).Should().NotBeNull();
        }

        [Test]
        public void Stage_3_warns_about_NINAs_filter_change_autofocus() {
            var nina = new StageEntry("AutofocusAfterFilterChange", "AF After Filter Change");
            var ours = new StageEntry(StageChecks.PlannerAfTrigger, PlannerAutofocusOnFilterChange.DisplayName);
            StageChecks.Check(StageKind.Triggers, new[] { nina }, new PlannerOptions()).Should().ContainSingle(w => w.Contains("Use \"AF after filter change (planner)\""));
            StageChecks.Check(StageKind.Triggers, new[] { nina, ours }, new PlannerOptions()).Should().ContainSingle(w => w.Contains("focus twice"));
            StageChecks.Check(StageKind.Triggers, new[] { ours }, new PlannerOptions()).Should().BeEmpty();
        }
    }
}
