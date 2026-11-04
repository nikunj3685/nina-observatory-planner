using FluentAssertions;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.ObservatoryPlanner.Core;
using NINA.ObservatoryPlanner.Nina;
using NINA.Sequencer.SequenceItem;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.ObservatoryPlanner.Tests {

    /// <summary>
    /// A stage is run the way the planner runs 1 Begin and 4 End: ResetAll, then Run. Steps (or a whole stage) switched off in the
    /// Advanced Sequencer must not execute: this checks it against NINA's own container, not a copy of its logic.
    /// </summary>
    [TestFixture]
    public class DisabledStepTests {
        private sealed class Mark : SequenceItem {
            private readonly List<string> ran;
            public Mark(string name, List<string> ran) { Name = name; this.ran = ran; }
            public override object Clone() => new Mark(Name, ran) { Status = Status };
            public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) { ran.Add(Name); return Task.CompletedTask; }
        }

        private static async Task<List<string>> RunLikeThePlanner(PlannerStageContainer stage) {
            // as NinaPlannerHardware.ContinueStage does before it runs a stage
            stage.ResetAll();
            await stage.Run(new Progress<ApplicationStatus>(), CancellationToken.None);
            return null;
        }

        private static PlannerStageContainer Stage(List<string> ran, params (string name, bool off)[] steps) {
            var stage = new PlannerStageContainer { Stage = StageKind.End };
            foreach (var (name, off) in steps) {
                var item = new Mark(name, ran);
                if (off) { item.Status = SequenceEntityStatus.DISABLED; }
                stage.Add(item);
            }
            return stage;
        }

        [Test]
        public async Task A_disabled_step_does_not_run_and_stays_disabled() {
            var ran = new List<string>();
            var stage = Stage(ran, ("Park", false), ("Close Dome", true), ("Warm Camera", false));
            await RunLikeThePlanner(stage);
            ran.Should().Equal("Park", "Warm Camera");
            stage.Items[1].Status.Should().Be(SequenceEntityStatus.DISABLED);
        }

        [Test]
        public async Task Disabled_steps_stay_off_on_every_run() {
            var ran = new List<string>();
            var stage = Stage(ran, ("A", true), ("B", false));
            await RunLikeThePlanner(stage);
            await RunLikeThePlanner(stage);
            ran.Should().Equal("B", "B");
        }

        // ---------- NINA's sequence file does not keep "disabled": the planner keeps the list itself ----------

        private static PlannerStageContainer Outer(List<string> ran, out PlannerStageContainer inner) {
            var outer = new PlannerStageContainer { Stage = StageKind.Begin };
            outer.Add(new Mark("first", ran));
            inner = new PlannerStageContainer { Stage = StageKind.End };
            inner.Add(new Mark("a", ran));
            inner.Add(new Mark("b", ran));
            outer.Add(inner);
            outer.Add(new Mark("last", ran));
            return outer;
        }

        [Test]
        public void The_positions_of_disabled_steps_are_listed_including_nested_ones() {
            var outer = Outer(new List<string>(), out var inner);
            outer.Items[0].Status = SequenceEntityStatus.DISABLED;
            inner.Items[1].Status = SequenceEntityStatus.DISABLED;
            WorkflowFiles.DisabledPaths(outer).Should().Equal("i0", "i1/i1");
            outer.Items[1].Status = SequenceEntityStatus.DISABLED;
            WorkflowFiles.DisabledPaths(outer).Should().Equal("i0", "i1", "i1/i1");
        }

        [Test]
        public void Switched_off_steps_come_back_after_the_workflow_is_saved_and_opened_again() {
            var folder = Path.Combine(Path.GetTempPath(), "op-dis-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try {
                var before = Outer(new List<string>(), out var inner);
                before.Items[2].Status = SequenceEntityStatus.DISABLED;
                inner.Items[0].Status = SequenceEntityStatus.DISABLED;
                var path = Path.Combine(folder, "My workflow.json");
                const string json = "{ \"steps\": 3 }"; // stands for what NINA's converter wrote; it holds no disabled flag
                File.WriteAllText(path, json);
                WorkflowFiles.WriteDisabled(path, json, WorkflowFiles.DisabledPaths(before));

                // a new tree, as NINA's converter builds it: everything is on
                var after = Outer(new List<string>(), out var innerAfter);
                WorkflowFiles.DisabledPaths(after).Should().BeEmpty();
                WorkflowFiles.ApplyDisabled(after, WorkflowFiles.ReadDisabled(path, json));
                after.Items[2].Status.Should().Be(SequenceEntityStatus.DISABLED);
                innerAfter.Items[0].Status.Should().Be(SequenceEntityStatus.DISABLED);
                innerAfter.Items[1].Status.Should().NotBe(SequenceEntityStatus.DISABLED);
                after.Items[0].Status.Should().NotBe(SequenceEntityStatus.DISABLED);
            } finally { Directory.Delete(folder, true); }
        }

        [Test]
        public void A_workflow_file_changed_by_something_else_gets_no_stale_list() {
            var folder = Path.Combine(Path.GetTempPath(), "op-dis-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try {
                var path = Path.Combine(folder, "w.json");
                WorkflowFiles.WriteDisabled(path, "old content", new[] { "i0", "i2" });
                WorkflowFiles.ReadDisabled(path, "old content").Should().BeEquivalentTo(new[] { "i0", "i2" });
                WorkflowFiles.ReadDisabled(path, "new content").Should().BeEmpty();
            } finally { Directory.Delete(folder, true); }
        }

        [Test]
        public void Nothing_switched_off_removes_the_list_and_a_missing_list_is_empty() {
            var folder = Path.Combine(Path.GetTempPath(), "op-dis-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try {
                var path = Path.Combine(folder, "w.json");
                WorkflowFiles.ReadDisabled(path, "x").Should().BeEmpty();
                WorkflowFiles.WriteDisabled(path, "x", new[] { "i1" });
                File.Exists(WorkflowFiles.DisabledFileFor(path)).Should().BeTrue();
                WorkflowFiles.WriteDisabled(path, "x", System.Array.Empty<string>());
                File.Exists(WorkflowFiles.DisabledFileFor(path)).Should().BeFalse();
                WorkflowFiles.DisabledFileFor(path).EndsWith(".json").Should().BeFalse("it must not be listed as a workflow");
            } finally { Directory.Delete(folder, true); }
        }

        [Test]
        public async Task A_disabled_stage_runs_nothing() {
            var ran = new List<string>();
            var stage = Stage(ran, ("A", false), ("B", false));
            stage.Status = SequenceEntityStatus.DISABLED;
            await RunLikeThePlanner(stage);
            ran.Should().BeEmpty();
        }
    }
}
