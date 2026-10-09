using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Sequencer.SequenceItem;
using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.ObservatoryPlanner.Nina {

    /// <summary>
    /// GS Server's AutoHome (home-sensor search) as an instruction, e.g. at the start of 1 Begin so every night starts from a
    /// verified home position. GS Server must be connected to the mount (connect the mount in NINA first).
    /// </summary>
    [ExportMetadata("Name", "GS Server AutoHome")]
    [ExportMetadata("Description", "Observatory Planner: runs GS Server's AutoHome with the mount's home sensors (e.g. CQ-350 Pro), so GS Server knows the true mount position. Connect the mount first.")]
    [ExportMetadata("Icon", "HomeSVG")]
    [ExportMetadata("Category", "Observatory Planner")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class GssAutoHomeInstruction : SequenceItem {

        [ImportingConstructor]
        public GssAutoHomeInstruction() { }

        private GssAutoHomeInstruction(GssAutoHomeInstruction cloneMe) : this() {
            CopyMetaData(cloneMe);
        }

        public override object Clone() => new GssAutoHomeInstruction(this);

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            progress?.Report(new ApplicationStatus { Status = "GS Server AutoHome…" });
            var error = await GssAutoHome.Run(token);
            if (error != null) { throw new NINA.Core.Model.SequenceEntityFailedException($"GS Server AutoHome failed: {error}"); }
        }

        public override TimeSpan GetEstimatedDuration() => TimeSpan.FromMinutes(3);

        public override string ToString() => $"Category: {Category}, Item: {nameof(GssAutoHomeInstruction)}";
    }
}
