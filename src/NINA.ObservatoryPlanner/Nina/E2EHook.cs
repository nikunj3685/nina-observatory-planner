using Newtonsoft.Json;
using NINA.Core.Utility;
using NINA.ObservatoryPlanner.Core;
using NINA.Sequencer.SequenceItem.Camera;
using NINA.Sequencer.SequenceItem.Utility;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace NINA.ObservatoryPlanner.Nina {

    /// <summary>
    /// Test-only: when OBSERVATORY_PLANNER_E2E points to a scenario file, load it after NINA starts, build the workflow
    /// and start the sequence, so the simulator tests run without clicking. Does nothing in normal use.
    /// </summary>
    internal static class E2EHook {
        public const string ScenarioVariable = "OBSERVATORY_PLANNER_E2E";

        internal class Scenario {
            public string Workflow { get; set; } = WorkflowLibrary.SafetyAndDome;
            public string TargetList { get; set; }
            public PlannerOptions Options { get; set; }
            /// <summary>Trigger types to remove from stage 3 (e.g. autofocus cannot work on simulated images).</summary>
            public List<string> RemoveTriggers { get; set; } = new();
            /// <summary>Shorten waits and camera cooling so a test night takes minutes.</summary>
            public bool Fast { get; set; } = true;
            public bool AutoStart { get; set; } = true;
            /// <summary>Start like a normal NINA launch (restore the last workflow, paused state, auto-start) instead of building the workflow.</summary>
            public bool Restore { get; set; }
        }

        public static string Status { get; private set; } = "off";

        /// <summary>Returns true when a test scenario is running (it then decides what happens at startup).</summary>
        public static bool StartIfRequested(PlannerService planner) {
#if !DEBUG
            // Release builds (the published plugin) never take test commands
            return false;
#else
            var path = Environment.GetEnvironmentVariable(ScenarioVariable);
            if (string.IsNullOrWhiteSpace(path)) { return false; }
            _ = Task.Run(async () => {
                try {
                    await Run(planner, path);
                } catch (Exception ex) {
                    Status = "failed: " + ex.Message;
                    Logger.Error("Observatory Planner E2E hook failed", ex);
                    planner.Log.Info("E2E hook failed: " + ex);
                }
            });
            return true;
#endif
        }

        private static async Task Run(PlannerService planner, string path) {
            var scenario = PlannerStore.Deserialize<Scenario>(File.ReadAllText(path));
            for (var i = 0; i < 120 && !planner.Nina.Sequence.Initialized; i++) { await Task.Delay(1000); }
            if (!planner.Nina.Sequence.Initialized) { throw new InvalidOperationException("The sequencer did not initialize within 2 minutes"); }

            if (scenario.Restore) {
                planner.Log.Info($"E2E scenario loaded: {Path.GetFileName(path)} (normal startup)");
                Status = "loaded";
                _ = Task.Run(() => FollowCommands(planner));
                await planner.RestoreAtStartupAsync();
                planner.Log.Info("E2E: startup restore finished");
                return;
            }
            if (scenario.Options != null) {
                var o = scenario.Options;
                planner.Options.RunMode = o.RunMode;
                planner.Options.GapMinutes = o.GapMinutes;
                planner.Options.GapMount = o.GapMount;
                planner.Options.GapCloseDome = o.GapCloseDome;
                planner.Options.SafeDelaySeconds = o.SafeDelaySeconds;
                planner.Options.AutofocusAfterBegin = o.AutofocusAfterBegin;
                planner.Options.CloseGuiderAppOnDisconnect = o.CloseGuiderAppOnDisconnect;
                planner.Options.CloseMountAppOnDisconnect = o.CloseMountAppOnDisconnect;
                planner.Options.DarkSunAltitude = o.DarkSunAltitude;
                planner.Options.KeepConnected.Clear();
                foreach (var d in o.KeepConnected) { planner.Options.KeepConnected.Add(d); }
            }
            if (!string.IsNullOrEmpty(scenario.TargetList)) { await Ui.Run(() => planner.OpenTargets(scenario.TargetList)); }

            await Ui.Run(() => {
                var container = WorkflowLibrary.Build(scenario.Workflow, planner);
                var triggers = container.Stage(StageKind.Triggers);
                foreach (var t in triggers.Triggers.Where(t => scenario.RemoveTriggers.Contains(t.GetType().Name)).ToList()) { triggers.Remove(t); }
                if (scenario.Fast) {
                    foreach (var stage in container.Items.OfType<PlannerStageContainer>()) {
                        foreach (var item in stage.Items) {
                            if (item is WaitForTimeSpan w) { w.Time = 2; }
                            if (item is CoolCamera c) { c.Duration = 0; }
                            if (item is WarmCamera wc) { wc.Duration = 0; }
                        }
                        // Wait for Time (astronomical dusk) would wait for real night: not useful in a test.
                        foreach (var wait in stage.Items.OfType<WaitForTime>().ToList()) { stage.Remove(wait); }
                    }
                }
                planner.Nina.Sequence.SetAdvancedSequence(WorkflowLibrary.NewSequence(container));
                planner.ShowPanelRequested?.Invoke();
            });
            planner.Log.Info($"E2E scenario loaded: {Path.GetFileName(path)} workflow={scenario.Workflow}");
            Status = "loaded";

            if (scenario.AutoStart) {
                await Task.Delay(2000);
                await Ui.Run(() => planner.StartRun(StartKind.Normal));
                Status = "started";
            }
            _ = Task.Run(() => FollowCommands(planner));
        }

        /// <summary>Test script → NINA: one command per line in e2e-commands.txt ("tab imaging", "tab sequence", "show panel").</summary>
        private static async Task FollowCommands(PlannerService planner) {
            var file = Path.Combine(planner.BaseRoot, "e2e-commands.txt");
            var done = 0;
            while (true) {
                await Task.Delay(500);
                if (!File.Exists(file)) { continue; }
                string[] lines;
                try { lines = File.ReadAllLines(file); } catch (IOException) { continue; }
                for (; done < lines.Length; done++) {
                    var cmd = lines[done].Trim().ToLowerInvariant();
                    await Ui.Run(() => {
                        try {
                            if (cmd == "tab imaging") { planner.Nina.Application.ChangeTab(NINA.Core.Enum.ApplicationTab.IMAGING); }
                            if (cmd == "tab sequence") {
                                planner.Nina.Application.ChangeTab(NINA.Core.Enum.ApplicationTab.SEQUENCE);
                                planner.Nina.Sequence.SwitchToAdvancedView();
                            }
                            if (cmd == "show panel") { planner.ShowPanelRequested?.Invoke(); }
                            if (cmd.StartsWith("planner tab ") && int.TryParse(cmd.Substring(12), out var tab)) { planner.ShowPlannerTabRequested?.Invoke(tab); }
                            if (cmd == "start") { planner.StartRun(StartKind.Normal); }
                            if (cmd.StartsWith("dialog ")) { planner.DialogRequested?.Invoke(cmd.Substring(7)); }
                            if (cmd == "autofocus") { planner.RequestAutofocus(); }
                            if (cmd == "pause now") { planner.RequestPause(PauseKind.Now); }
                            if (cmd == "pause after frame") { planner.RequestPause(PauseKind.AfterFrame); }
                            if (cmd == "cancel pause") { planner.CancelPause(); }
                            if (cmd == "start sequence") { planner.Resume(); }
                            if (cmd == "stop planner") { planner.Stop(); }
                            if (cmd == "report sequence") {
                                planner.Log.Info($"E2E: NINA sequence running={planner.Nina.Sequence.IsAdvancedSequenceRunning()} paused={planner.IsPaused} running={planner.IsRunning}");
                            }
                            if (cmd.StartsWith("edit exposure filter ")) {
                                var e = planner.Targets.FirstOrDefault()?.Exposures.FirstOrDefault();
                                if (e != null) { e.Filter = lines[done].Trim().Substring("edit exposure filter ".Length); }
                            }
                            if (cmd == "move target 2 first" && planner.Targets.Count > 1) { planner.Targets.Move(1, 0); }
                            if (cmd == "disconnect camera") { _ = planner.Nina.Camera.Disconnect(); }
                            if (cmd == "slew away") {
                                var info = planner.Nina.Telescope.GetInfo();
                                var c = info.Coordinates;
                                var away = new NINA.Astrometry.Coordinates(NINA.Astrometry.Angle.ByHours((c.RA + 1.5) % 24), NINA.Astrometry.Angle.ByDegree(Math.Max(-30, c.Dec - 20)), c.Epoch);
                                _ = planner.Nina.Telescope.SlewToCoordinatesAsync(away, System.Threading.CancellationToken.None);
                            }
                            if (cmd == "set autostart on") { planner.Options.AutoStartOnLaunch = true; }
                            if (cmd == "stop") { planner.Nina.Sequence.CancelAdvancedSequence(); }
                            if (cmd.StartsWith("slew target ") || cmd.StartsWith("center target ") || cmd.StartsWith("select target ") || cmd.StartsWith("rename target ")) { planner.DialogRequested?.Invoke(cmd); }
                            if (cmd == "report stages") { planner.DialogRequested?.Invoke(cmd); }
                            if (cmd.StartsWith("answer ")) {
                                foreach (System.Windows.Window w in System.Windows.Application.Current.Windows) {
                                    if (w is UI.Dialogs.AskWindow ask) { ask.Answer(cmd.Substring(7)); }
                                }
                            }
                            if (cmd == "edit stage 1") {
                                // what a user does in the Advanced Sequencer: add an instruction to 1 Begin
                                var begin = planner.ActiveContainer?.Stage(StageKind.Begin);
                                var wait = (WaitForTimeSpan)planner.Factory.WaitSeconds(7);
                                begin?.Add(wait);
                            }
                            if (cmd == "replace sequence") {
                                // a different sequence opened in the Advanced Sequencer (File > Open / Load workflow elsewhere)
                                var other = WorkflowLibrary.Build(WorkflowLibrary.NoSafetyNoDome, planner);
                                other.Stage(StageKind.End).Add(planner.Factory.WaitSeconds(9));
                                planner.Nina.Sequence.SetAdvancedSequence(WorkflowLibrary.NewSequence(other));
                            }
                            if (cmd == "close dialogs") {
                                foreach (System.Windows.Window w in System.Windows.Application.Current.Windows) {
                                    if (w.GetType().Namespace == typeof(UI.Dialogs.AskWindow).Namespace) { w.Close(); }
                                }
                            }
                        } catch (Exception ex) { Logger.Error(ex); }
                    });
                    planner.Log.Info($"E2E command: {cmd}");
                }
            }
        }
    }
}
