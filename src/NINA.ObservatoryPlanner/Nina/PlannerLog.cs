using Newtonsoft.Json;
using NINA.Core.Utility;
using NINA.ObservatoryPlanner.Core;
using System;
using System.IO;

namespace NINA.ObservatoryPlanner.Nina {

    /// <summary>
    /// Writes what the planner does to NINA's log and to a JSON-lines file (one event per line),
    /// and updates the status shown in the panel.
    /// </summary>
    public class PlannerLog : IPlannerLog {
        private readonly string folder;
        private readonly PlannerService service;
        private readonly object fileLock = new();
        private string path;

        public PlannerLog(string folder, PlannerService service) {
            this.folder = folder;
            this.service = service;
        }

        public string CurrentFile => path;

        public void StartRun(PlannerOptions options) {
            Write(new { kind = "run", mode = options.RunMode.ToString(), gapMinutes = options.GapMinutes, gapMount = options.GapMount.ToString() });
        }

        public void Phase(PlannerPhase phase, string message, PlannerTarget target = null) {
            Logger.Info($"Observatory Planner: {message}");
            service?.SetPhase(phase, message, target);
            Write(new { kind = "phase", phase = phase.ToString(), message, target = target?.Name });
        }

        public void Status(string message) => service?.SetStatus(message);

        public void Info(string message) {
            Logger.Info($"Observatory Planner: {message}");
            Write(new { kind = "info", message });
        }

        public void Frame(PlannerTarget target, PlannerExposure exposure) {
            Write(new { kind = "frame", target = target.Name, filter = exposure.Filter, done = exposure.Done, count = exposure.Count, targetDone = target.DoneFrames, targetTotal = target.TotalFrames });
        }

        // One file per NINA session, created on the first event, so nothing logged before a run starts is lost.
        private void Write(object evt) {
            if (path == null) {
                lock (fileLock) {
                    if (path == null) {
                        try {
                            Directory.CreateDirectory(folder);
                            path = Path.Combine(folder, $"planner-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
                        } catch (Exception ex) { Logger.Error(ex); return; }
                    }
                }
            }
            var line = JsonConvert.SerializeObject(new { time = DateTime.Now.ToString("O"), evt });
            lock (fileLock) {
                try { File.AppendAllText(path, line + Environment.NewLine); } catch (Exception ex) { Logger.Error(ex); }
            }
        }
    }
}
