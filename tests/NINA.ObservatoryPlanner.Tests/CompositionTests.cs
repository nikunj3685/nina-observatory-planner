using FluentAssertions;
using NINA.ObservatoryPlanner.Nina;
using NINA.Sequencer.Trigger;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.IO;
using System.Runtime.CompilerServices;

namespace NINA.ObservatoryPlanner.Tests {

    [TestFixture]
    [NonParallelizable]
    public class CompositionTests {

        [Test]
        public void Manifest_and_panel_containers_share_one_planner() {
            var root = Path.Combine(Path.GetTempPath(), "op-composition-" + Guid.NewGuid().ToString("N"));
            var previous = Environment.GetEnvironmentVariable("OBSERVATORY_PLANNER_ROOT");
            Environment.SetEnvironmentVariable("OBSERVATORY_PLANNER_ROOT", root);
            try {
                // NINA's PluginLoader composes the manifest and the other plugin parts in two separate containers.
                var nina = (NinaServices)RuntimeHelpers.GetUninitializedObject(typeof(NinaServices));
                PlannerService Compose() {
                    var container = new CompositionContainer(new TypeCatalog(typeof(PlannerServiceExport)));
                    container.ComposeExportedValue(nina);
                    return container.GetExportedValue<PlannerService>();
                }

                var first = Compose();
                var second = Compose();

                first.Should().NotBeNull();
                second.Should().BeSameAs(first);
            } finally {
                Environment.SetEnvironmentVariable("OBSERVATORY_PLANNER_ROOT", previous);
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }

        [Test]
        public void The_planner_autofocus_trigger_is_exported_with_the_shared_planner() {
            var root = Path.Combine(Path.GetTempPath(), "op-composition-" + Guid.NewGuid().ToString("N"));
            var previous = Environment.GetEnvironmentVariable("OBSERVATORY_PLANNER_ROOT");
            Environment.SetEnvironmentVariable("OBSERVATORY_PLANNER_ROOT", root);
            try {
                var nina = (NinaServices)RuntimeHelpers.GetUninitializedObject(typeof(NinaServices));
                var container = new CompositionContainer(new TypeCatalog(typeof(PlannerServiceExport), typeof(PlannerAutofocusOnFilterChange)));
                container.ComposeExportedValue(nina);
                var export = container.GetExports<ISequenceTrigger, IDictionary<string, object>>().Should().ContainSingle().Subject;
                export.Metadata["Name"].Should().Be(PlannerAutofocusOnFilterChange.DisplayName);
                export.Metadata["Category"].Should().Be("Observatory Planner");
                export.Value.Should().BeOfType<PlannerAutofocusOnFilterChange>();
            } finally {
                Environment.SetEnvironmentVariable("OBSERVATORY_PLANNER_ROOT", previous);
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }
    }
}
