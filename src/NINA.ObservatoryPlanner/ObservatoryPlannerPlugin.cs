using NINA.ObservatoryPlanner.Nina;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using System.ComponentModel.Composition;
using System.Threading.Tasks;

namespace NINA.ObservatoryPlanner {

    /// <summary>Plugin manifest. Name, version and description come from the assembly attributes.</summary>
    [Export(typeof(IPluginManifest))]
    public class ObservatoryPlannerPlugin : PluginBase {
        private readonly PlannerService planner;

        [ImportingConstructor]
        public ObservatoryPlannerPlugin(PlannerService planner) {
            this.planner = planner;
        }

        public override Task Initialize() {
            if (!E2EHook.StartIfRequested(planner)) {
                // reopen this profile's last workflow; come back paused or auto-start as set
                _ = System.Threading.Tasks.Task.Run(async () => {
                    try { await planner.RestoreAtStartupAsync(); } catch (System.Exception ex) { NINA.Core.Utility.Logger.Error(ex); }
                });
            }
            return Task.CompletedTask;
        }
    }
}
