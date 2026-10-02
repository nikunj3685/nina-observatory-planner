using NINA.Sequencer;
using NINA.Sequencer.SequenceItem;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NINA.ObservatoryPlanner.Nina {

    /// <summary>
    /// One-line description of an instruction's settings for the Equipment &amp; Safety stage boxes,
    /// e.g. "Power1 → On", "−10 °C · 5 min", "every 1 frame". Properties are read by name so instructions from
    /// other plugins and later NINA versions still show their common settings.
    /// </summary>
    internal static class InstructionSummary {

        public static string CategoryOf(ISequenceEntity e) {
            var assembly = e.GetType().Assembly.GetName().Name;
            if (assembly != "NINA.Sequencer" && assembly != typeof(InstructionSummary).Assembly.GetName().Name) { return "plugin"; }
            return string.IsNullOrWhiteSpace(e.Category) ? "" : e.Category;
        }

        public static string Of(ISequenceEntity e, bool targetStage) {
            var parts = new List<string>();
            switch (e.GetType().Name) {
                case "SetSwitchValue": {
                        var sw = Get(e, "SelectedSwitch");
                        var name = Get(sw, "Name") as string ?? $"switch {Get(e, "SwitchIndex")}";
                        var value = Num(Get(e, "Value"));
                        var isOnOff = Num(Get(sw, "Minimum")) == 0 && Num(Get(sw, "Maximum")) == 1;
                        parts.Add($"{name} → {(isOnOff ? (value >= 0.5 ? "On" : "Off") : Fmt(value))}");
                        break;
                    }
                case "WaitForTimeSpan": parts.Add($"{Fmt(Num(Get(e, "Time")))} s"); break;
                case "WaitForTime": {
                        var provider = Get(Get(e, "SelectedProvider"), "Name") as string;
                        parts.Add(provider ?? $"{Get(e, "Hours"):00}:{Get(e, "Minutes"):00}");
                        break;
                    }
                case "CoolCamera": parts.Add($"{Fmt(Num(Get(e, "Temperature")))} °C"); parts.Add($"{Fmt(Num(Get(e, "Duration")))} min"); break;
                case "WarmCamera": parts.Add($"{Fmt(Num(Get(e, "Duration")))} min"); break;
                case "StartGuiding": if (Get(e, "ForceCalibration") is true) { parts.Add("force calibration"); } break;
                case "DitherAfterExposures": {
                        var n = Num(Get(e, "AfterExposures"));
                        parts.Add(n == 1 ? "every frame" : $"every {Fmt(n)} frames");
                        break;
                    }
                case "ConnectEquipment" or "DisconnectEquipment": parts.Add(InstructionFactory.DeviceOf((ISequenceItem)e)); break;
                case "SetTracking": parts.Add(Get(e, "TrackingMode")?.ToString()); break;
                case "Center" or "CenterAndRotate" or "SlewScopeToRaDec":
                    if (targetStage) { parts.Add("current target"); }
                    if (e.GetType().Name == "CenterAndRotate") { parts.Add($"PA {Fmt(Num(Get(e, "PositionAngle")))}°"); }
                    break;
                case "TakeExposure" or "TakeManyExposures" or "SmartExposure":
                    parts.Add($"{Fmt(Num(Get(e, "ExposureTime")))} s");
                    break;
                case "SwitchFilter": parts.Add(Get(Get(e, "Filter"), "Name") as string); break;
                case "Annotation": parts.Add(Trim(Get(e, "Text") as string)); break;
            }
            if (e is ISequenceItem item) {
                if (item.Attempts > 1) { parts.Add($"{item.Attempts} attempts"); }
                if (item.ErrorBehavior != NINA.Sequencer.Utility.InstructionErrorBehavior.ContinueOnError) { parts.Add($"on error: {Words(item.ErrorBehavior.ToString())}"); }
            }
            return string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }

        private static object Get(object o, string property) {
            if (o == null) { return null; }
            try { return o.GetType().GetProperty(property)?.GetValue(o); } catch (Exception) { return null; }
        }

        private static double Num(object o) => o switch {
            null => double.NaN,
            IConvertible c => Convert.ToDouble(c, CultureInfo.InvariantCulture),
            _ => double.NaN
        };

        private static string Fmt(double v) => double.IsNaN(v) ? "?" : v.ToString("0.##", CultureInfo.CurrentCulture).Replace("-", "−");

        private static string Trim(string s) => s == null ? null : s.Length > 40 ? s.Substring(0, 38) + "…" : s;

        private static string Words(string pascal) => string.Concat(pascal.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : c.ToString()));
    }
}
