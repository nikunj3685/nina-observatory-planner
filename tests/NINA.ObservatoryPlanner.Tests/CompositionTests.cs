using FluentAssertions;
using NINA.ObservatoryPlanner.Nina;
using NUnit.Framework;
using System;
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
    }
}
