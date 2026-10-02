using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.IO;

namespace NINA.ObservatoryPlanner.Core {

    /// <summary>
    /// Saves target lists and options as JSON. Target lists are plain files the user can back up or copy;
    /// the last opened list is reopened when NINA starts, and progress is written after every frame.
    /// </summary>
    public class PlannerStore {
        private static readonly JsonSerializerSettings Settings = new() {
            Formatting = Formatting.Indented,
            Converters = { new StringEnumConverter() },
            ObjectCreationHandling = ObjectCreationHandling.Replace
        };

        public string Root { get; }
        public string TargetsFolder => Path.Combine(Root, "Target lists");
        public string WorkflowsFolder => Path.Combine(Root, "Workflows");
        private string OptionsPath => Path.Combine(Root, "options.json");
        private string LastListPath => Path.Combine(Root, "last-target-list.txt");
        private string StatePath => Path.Combine(Root, "state.json");

        /// <summary>
        /// The folder of one NINA profile: &lt;base&gt;\Profiles\&lt;profile id&gt;. The first time a profile is used, the planner's
        /// earlier shared settings and last target list are carried over so nothing is lost.
        /// </summary>
        public static PlannerStore ForProfile(string baseRoot, string profileId) {
            if (string.IsNullOrWhiteSpace(profileId)) { return new PlannerStore(baseRoot); }
            var root = Path.Combine(baseRoot, "Profiles", profileId);
            if (!File.Exists(Path.Combine(root, "options.json"))) {
                Directory.CreateDirectory(root);
                foreach (var name in new[] { "options.json", "last-target-list.txt" }) {
                    var shared = Path.Combine(baseRoot, name);
                    if (File.Exists(shared)) { File.Copy(shared, Path.Combine(root, name), overwrite: false); }
                }
            }
            return new PlannerStore(root);
        }

        public PlannerStore(string root) {
            Root = root;
            Directory.CreateDirectory(TargetsFolder);
            Directory.CreateDirectory(WorkflowsFolder);
        }

        /// <summary>Documents\N.I.N.A\Observatory Planner, or OBSERVATORY_PLANNER_ROOT when set (tests use their own folder).</summary>
        public static string DefaultRoot() {
            var overridden = Environment.GetEnvironmentVariable("OBSERVATORY_PLANNER_ROOT");
            return !string.IsNullOrWhiteSpace(overridden)
                ? overridden
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "N.I.N.A", "Observatory Planner");
        }

        public static string Serialize(object value) => JsonConvert.SerializeObject(value, Settings);
        public static T Deserialize<T>(string json) => JsonConvert.DeserializeObject<T>(json, Settings);

        public PlannerOptions LoadOptions() {
            try {
                if (File.Exists(OptionsPath)) { return Deserialize<PlannerOptions>(File.ReadAllText(OptionsPath)) ?? new PlannerOptions(); }
            } catch (Exception) {
                // a damaged file must not stop the plugin from loading; fall back to defaults
            }
            return new PlannerOptions();
        }

        public void SaveOptions(PlannerOptions options) => WriteAtomic(OptionsPath, Serialize(options));

        public TargetList LoadTargetList(string path) => Deserialize<TargetList>(File.ReadAllText(path)) ?? new TargetList();

        public void SaveTargetList(TargetList list, string path) {
            WriteAtomic(path, Serialize(list));
            File.WriteAllText(LastListPath, path);
        }

        public PlannerState LoadState() {
            try { if (File.Exists(StatePath)) { return Deserialize<PlannerState>(File.ReadAllText(StatePath)) ?? new PlannerState(); } } catch (Exception) { }
            return new PlannerState();
        }

        public void SaveState(PlannerState state) => WriteAtomic(StatePath, Serialize(state));

        public static void WriteText(string path, string content) => WriteAtomic(path, content);

        public string LastTargetListPath() {
            try {
                var p = File.Exists(LastListPath) ? File.ReadAllText(LastListPath).Trim() : null;
                return p != null && File.Exists(p) ? p : null;
            } catch (Exception) {
                return null;
            }
        }

        // Write to a temp file first so a crash mid-write never leaves a half-written list.
        private static void WriteAtomic(string path, string content) {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, content);
            if (File.Exists(path)) { File.Replace(tmp, path, null); } else { File.Move(tmp, path); }
        }
    }
}
