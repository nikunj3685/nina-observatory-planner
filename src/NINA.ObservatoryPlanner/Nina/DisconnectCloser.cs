using Microsoft.Win32;
using NINA.Core.Utility;
using NINA.ObservatoryPlanner.Core;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.ObservatoryPlanner.Nina {

    /// <summary>What the closer needs from PHD2.</summary>
    internal interface IPhd2Control {
        /// <summary>True when PHD2's server answers.</summary>
        Task<bool> IsRunning(CancellationToken token);
        /// <summary>Stops looping and guiding and disconnects PHD2's own equipment; with <paramref name="closeApp"/> PHD2 then closes itself.</summary>
        Task ShutDown(bool closeApp, CancellationToken token);
    }

    /// <summary>
    /// PHD2's JSON-RPC server (the one NINA uses, PHD2 › Tools › Enable Server). NINA's own "disconnect" only closes its
    /// connection: PHD2 stays open with its guide camera and mount connected, which also keeps the mount's ASCOM program
    /// (e.g. GS Server) running.
    /// </summary>
    internal sealed class Phd2Client : IPhd2Control {
        private readonly Func<(string Host, int Port)> endpoint;
        private readonly Func<TimeSpan, CancellationToken, Task> delay;

        public Phd2Client(Func<(string Host, int Port)> endpoint, Func<TimeSpan, CancellationToken, Task> delay = null) {
            this.endpoint = endpoint;
            this.delay = delay ?? ((d, t) => Task.Delay(d, t));
        }

        /// <summary>How long PHD2 may take to answer one request.</summary>
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);
        /// <summary>How long to wait for PHD2 to close after "shutdown".</summary>
        public TimeSpan ExitTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// PHD2 listens on IPv4 only. "localhost" also resolves to IPv6 ::1, and on Windows a refused connection to ::1
        /// takes about 2 s, so connect to the IPv4 address directly, as NINA does.
        /// </summary>
        internal static async Task<IPAddress> ResolveIPv4(string host, CancellationToken token) {
            if (string.IsNullOrWhiteSpace(host) || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) { return IPAddress.Loopback; }
            if (IPAddress.TryParse(host, out var ip)) { return ip; }
            var all = await Dns.GetHostAddressesAsync(host, token);
            return all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? all.FirstOrDefault();
        }

        private async Task<TcpClient> Connect(TimeSpan timeout, CancellationToken token) {
            var (host, port) = endpoint();
            TcpClient client = null;
            try {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(timeout);
                var ip = await ResolveIPv4(host, cts.Token);
                if (ip == null) { return null; }
                client = new TcpClient(ip.AddressFamily);
                await client.ConnectAsync(ip, port, cts.Token);
                return client;
            } catch (Exception) when (!token.IsCancellationRequested) {
                client?.Dispose();
                return null;
            }
        }

        public async Task<bool> IsRunning(CancellationToken token) {
            using var client = await Connect(TimeSpan.FromSeconds(5), token);
            return client != null;
        }

        public async Task ShutDown(bool closeApp, CancellationToken token) {
            using var client = await Connect(TimeSpan.FromSeconds(5), token);
            if (client == null) { return; }
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
            var id = 0;

            async Task Call(string method, string parameters = null) {
                var thisId = ++id;
                await writer.WriteLineAsync($"{{\"method\":\"{method}\"{(parameters == null ? "" : ",\"params\":" + parameters)},\"id\":{thisId}}}");
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(RequestTimeout);
                // PHD2 also sends events on this connection; read until the answer to this request
                while (true) {
                    var line = await reader.ReadLineAsync(cts.Token);
                    if (line == null) { return; }
                    if (line.Contains("\"jsonrpc\"") && line.Contains($"\"id\":{thisId}")) {
                        if (line.Contains("\"error\"")) { Logger.Warning($"Observatory Planner: PHD2 {method}: {line}"); }
                        return;
                    }
                }
            }

            await Call("stop_capture");
            await Call("set_connected", "[false]");
            if (!closeApp) { return; }
            try { await Call("shutdown"); } catch (IOException) { } // PHD2 may close the connection while answering
            var until = DateTime.UtcNow + ExitTimeout;
            while (DateTime.UtcNow < until) {
                await delay(TimeSpan.FromSeconds(1), token);
                if (!await IsRunning(token)) { return; }
            }
            Logger.Warning("Observatory Planner: PHD2 did not close within 30 s after shutdown");
        }
    }

    /// <summary>Finds the program behind an ASCOM driver (its COM local server), e.g. GS Server for "ASCOM.GS.Sky.Telescope".</summary>
    internal static class AscomDriverProgram {

        /// <summary>The .exe of the driver's local server, or null (a DLL driver runs inside NINA, an Alpaca device has none).</summary>
        public static string ExeFor(string progId) {
            if (string.IsNullOrWhiteSpace(progId)) { return null; }
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 }) {
                try {
                    using var root = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
                    var clsid = root.OpenSubKey(progId + "\\CLSID")?.GetValue(null) as string;
                    if (string.IsNullOrWhiteSpace(clsid)) { continue; }
                    var server = root.OpenSubKey($"CLSID\\{clsid}\\LocalServer32")?.GetValue(null) as string;
                    var exe = ParseLocalServer(server);
                    if (exe != null) { return exe; }
                } catch (Exception ex) { Logger.Error(ex); }
            }
            return null;
        }

        /// <summary>The program path of a LocalServer32 value: <c>"C:\x y\GS.Server.exe" /embedding</c> → <c>C:\x y\GS.Server.exe</c>.</summary>
        public static string ParseLocalServer(string value) {
            if (string.IsNullOrWhiteSpace(value)) { return null; }
            value = value.Trim();
            if (value.StartsWith("\"")) {
                var end = value.IndexOf('"', 1);
                return end > 1 ? value.Substring(1, end - 1) : null;
            }
            var exe = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return exe > 0 ? value.Substring(0, exe + 4) : value.Split(' ')[0];
        }

        /// <summary>True while a process of this program runs.</summary>
        public static bool IsRunning(string exePath) {
            var name = Path.GetFileNameWithoutExtension(exePath);
            var processes = Process.GetProcessesByName(name);
            try {
                return processes.Any(p => {
                    try { return string.Equals(p.MainModule?.FileName, exePath, StringComparison.OrdinalIgnoreCase); } catch (Exception) { return true; }
                });
            } finally {
                foreach (var p in processes) { p.Dispose(); }
            }
        }
    }

    /// <summary>
    /// ⚙ Options "Close PHD2" and "Close the mount software": run as part of NINA's own Disconnect Equipment (Guider,
    /// Mount) and Disconnect All, wherever those instructions are, while NINA's sequence runs. NINA waits for this before
    /// the instruction finishes. Order: PHD2 first (it holds a connection to the mount's program), then the mount software,
    /// which, like any ASCOM local server, exits by itself once no program uses it.
    /// </summary>
    internal sealed class DisconnectCloser {
        private readonly PlannerOptions options;
        private readonly Func<bool> sequenceRunning;
        private readonly Func<bool> mountDisconnectIntended;
        private readonly Func<bool> guiderIsPhd2;
        private readonly Func<bool> guiderConnectedInNina;
        private readonly Func<Task> disconnectNinaGuider;
        private readonly IPhd2Control phd2;
        private readonly Func<string> mountProgram;
        private readonly Func<string, bool> programRunning;
        private readonly Func<TimeSpan, CancellationToken, Task> delay;
        private readonly Action<string> info;
        private readonly Action<string> warn;

        public DisconnectCloser(PlannerOptions options, Func<bool> sequenceRunning, Func<bool> mountDisconnectIntended, Func<bool> guiderIsPhd2, Func<bool> guiderConnectedInNina, Func<Task> disconnectNinaGuider,
                                IPhd2Control phd2, Func<string> mountProgram, Func<string, bool> programRunning,
                                Func<TimeSpan, CancellationToken, Task> delay, Action<string> info, Action<string> warn) {
            this.options = options;
            this.sequenceRunning = sequenceRunning;
            this.mountDisconnectIntended = mountDisconnectIntended;
            this.guiderIsPhd2 = guiderIsPhd2;
            this.guiderConnectedInNina = guiderConnectedInNina;
            this.disconnectNinaGuider = disconnectNinaGuider;
            this.phd2 = phd2;
            this.mountProgram = mountProgram;
            this.programRunning = programRunning;
            this.delay = delay;
            this.info = info;
            this.warn = warn;
        }

        // NINA also raises "disconnected" when it connects a device (it first disconnects the old, unconnected one):
        // only a disconnect after a real connection counts
        private volatile bool guiderWasConnected;
        private volatile bool mountWasConnected;
        public void GuiderConnected() => guiderWasConnected = true;
        public void MountConnected() => mountWasConnected = true;

        /// <summary>How long the mount software may take to exit after the last program let go of it.</summary>
        public TimeSpan MountExitWait { get; set; } = TimeSpan.FromSeconds(30);

        public async Task OnGuiderDisconnected() {
            if (!guiderWasConnected) { return; }
            guiderWasConnected = false;
            if (!options.CloseGuiderAppOnDisconnect || !sequenceRunning()) { return; }
            // only when NINA's guider is PHD2: another program may use a PHD2 that is open next to NINA
            if (!guiderIsPhd2() || !await phd2.IsRunning(CancellationToken.None)) { return; }
            info("Guider disconnected: PHD2 disconnects its equipment and closes");
            await phd2.ShutDown(closeApp: true, CancellationToken.None);
        }

        /// <summary>
        /// 4 End has finished and disconnects the guider: PHD2 may still be open although NINA's guider was never connected
        /// in this run (then "Disconnect Equipment" raises no disconnect event). Close it now.
        /// </summary>
        public async Task OnEndFinished() {
            if (!options.CloseGuiderAppOnDisconnect || guiderConnectedInNina() || !guiderIsPhd2()) { return; }
            if (!await phd2.IsRunning(CancellationToken.None)) { return; }
            info("4 End: the guider is disconnected but PHD2 is still open: PHD2 disconnects its equipment and closes");
            await phd2.ShutDown(closeApp: true, CancellationToken.None);
        }

        public async Task OnMountDisconnected() {
            if (!mountWasConnected) { return; }
            mountWasConnected = false;
            // a lost mount (its software stopped) is not a disconnect: PHD2 must stay open for the recovery
            if (!mountDisconnectIntended()) { return; }
            if (!options.CloseMountAppOnDisconnect || !sequenceRunning()) { return; }
            // PHD2 first: it guides through the mount's program and keeps it open
            if (guiderIsPhd2() && await phd2.IsRunning(CancellationToken.None)) {
                if (options.CloseGuiderAppOnDisconnect && guiderConnectedInNina()) {
                    info("Mount disconnected: disconnecting the guider first, so PHD2 lets go of the mount");
                    await disconnectNinaGuider(); // NINA then raises "guider disconnected", which closes PHD2
                }
                if (await phd2.IsRunning(CancellationToken.None)) {
                    info(options.CloseGuiderAppOnDisconnect
                        ? "Mount disconnected: PHD2 disconnects its equipment and closes first"
                        : "Mount disconnected: PHD2 disconnects its equipment first (it stays open)");
                    await phd2.ShutDown(closeApp: options.CloseGuiderAppOnDisconnect, CancellationToken.None);
                }
            }

            var program = mountProgram();
            if (program == null) { return; } // a driver inside NINA, or an Alpaca mount: nothing to close
            var name = Path.GetFileName(program);
            var waited = TimeSpan.Zero;
            while (programRunning(program)) {
                if (waited >= MountExitWait) {
                    warn($"{name} is still open {MountExitWait.TotalSeconds:0} s after the mount was disconnected: another program may still be connected to it. It was left open.");
                    return;
                }
                await delay(TimeSpan.FromSeconds(1), CancellationToken.None);
                waited += TimeSpan.FromSeconds(1);
            }
            info($"Mount disconnected: {name} has closed");
        }
    }
}
