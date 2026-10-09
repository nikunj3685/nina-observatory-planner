using FluentAssertions;
using NINA.ObservatoryPlanner.Core;
using NINA.ObservatoryPlanner.Nina;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.ObservatoryPlanner.Tests {

    /// <summary>Item 14: disconnecting the guider closes PHD2; disconnecting the mount lets its ASCOM program (GS Server) close.</summary>
    [TestFixture]
    public class DisconnectCloserTests {

        private sealed class FakePhd2 : IPhd2Control {
            public bool Running = true;
            public readonly List<string> Calls;
            public FakePhd2(List<string> calls) { Calls = calls; }
            public Task<bool> IsRunning(CancellationToken token) => Task.FromResult(Running);
            public Task ShutDown(bool closeApp, CancellationToken token) {
                Calls.Add(closeApp ? "phd2 release+close" : "phd2 release");
                if (closeApp) { Running = false; }
                return Task.CompletedTask;
            }
        }

        private sealed class Rig {
            public readonly List<string> Calls = new();
            public readonly List<string> Warnings = new();
            public readonly PlannerOptions Options = new();
            public FakePhd2 Phd2;
            public bool SequenceRunning = true;
            public bool GuiderIsPhd2 = true;
            public bool GuiderConnected = true;
            public string MountProgram = @"C:\Program Files\GSS\GS.Server.exe";
            public int MountExitsAfterSeconds = 2;
            public int Waited;
            public DisconnectCloser Closer;

            public Rig() {
                Phd2 = new FakePhd2(Calls);
                Closer = new DisconnectCloser(Options, () => SequenceRunning, () => GuiderIsPhd2, () => GuiderConnected,
                    async () => { Calls.Add("nina disconnects guider"); GuiderConnected = false; await Closer.OnGuiderDisconnected(); },
                    Phd2, () => MountProgram, _ => Waited < MountExitsAfterSeconds,
                    (d, t) => { Waited += (int)d.TotalSeconds; return Task.CompletedTask; }, m => Calls.Add("info: " + m), Warnings.Add);
            }
        }

        [Test]
        public async Task Disconnecting_the_guider_closes_PHD2_with_its_equipment() {
            var rig = new Rig();
            await rig.Closer.OnGuiderDisconnected();
            rig.Calls.Should().Contain("phd2 release+close");
        }

        [Test]
        public async Task Nothing_happens_outside_a_running_sequence_or_with_the_options_off() {
            var rig = new Rig { SequenceRunning = false };
            await rig.Closer.OnGuiderDisconnected();
            await rig.Closer.OnMountDisconnected();
            rig.Calls.Should().BeEmpty("a disconnect by hand in NINA's Equipment tab is left alone");

            rig.SequenceRunning = true;
            rig.Options.CloseGuiderAppOnDisconnect = false;
            rig.Options.CloseMountAppOnDisconnect = false;
            await rig.Closer.OnGuiderDisconnected();
            await rig.Closer.OnMountDisconnected();
            rig.Calls.Should().BeEmpty();
        }

        [Test]
        public async Task A_PHD2_open_next_to_NINA_is_left_alone_when_NINA_uses_another_guider() {
            var rig = new Rig { GuiderIsPhd2 = false };
            await rig.Closer.OnGuiderDisconnected();
            await rig.Closer.OnMountDisconnected();
            rig.Calls.Should().NotContain(c => c.StartsWith("phd2") || c == "nina disconnects guider");
            rig.Phd2.Running.Should().BeTrue();
            rig.Calls.Should().Contain("info: Mount disconnected: GS.Server.exe has closed", "the mount software is still handled");
        }

        [Test]
        public async Task PHD2_left_open_with_the_guider_never_connected_is_closed_when_4_End_finishes() {
            var rig = new Rig { GuiderConnected = false };
            await rig.Closer.OnEndFinished();
            rig.Calls.Should().Contain("phd2 release+close");

            var connected = new Rig { GuiderConnected = true };
            await connected.Closer.OnEndFinished();
            connected.Calls.Should().BeEmpty("a connected guider is closed by its own disconnect");
            var other = new Rig { GuiderConnected = false, GuiderIsPhd2 = false };
            await other.Closer.OnEndFinished();
            other.Calls.Should().BeEmpty("NINA does not guide with PHD2");
        }

        [Test]
        public async Task Mount_first_in_4_End_still_closes_PHD2_before_the_mount_software() {
            var rig = new Rig();
            await rig.Closer.OnMountDisconnected();
            rig.Calls.Should().ContainInOrder("nina disconnects guider", "phd2 release+close", "info: Mount disconnected: GS.Server.exe has closed");
            rig.Warnings.Should().BeEmpty();
        }

        [Test]
        public async Task With_PHD2_kept_open_it_still_lets_go_of_the_mount() {
            var rig = new Rig();
            rig.Options.CloseGuiderAppOnDisconnect = false;
            await rig.Closer.OnMountDisconnected();
            rig.Calls.Should().ContainInOrder("phd2 release", "info: Mount disconnected: GS.Server.exe has closed");
            rig.Calls.Should().NotContain("nina disconnects guider");
            rig.Phd2.Running.Should().BeTrue();
        }

        [Test]
        public async Task Mount_software_still_open_after_30_s_is_left_open_with_a_note() {
            var rig = new Rig { MountExitsAfterSeconds = 1000 };
            await rig.Closer.OnMountDisconnected();
            rig.Waited.Should().Be(30);
            rig.Warnings.Should().ContainSingle().Which.Should().Contain("GS.Server.exe is still open 30 s after the mount was disconnected").And.Contain("left open");
        }

        [Test]
        public async Task A_mount_without_its_own_program_has_nothing_to_close() {
            var rig = new Rig { MountProgram = null };
            rig.Phd2.Running = false;
            await rig.Closer.OnMountDisconnected();
            rig.Calls.Should().BeEmpty();
            rig.Warnings.Should().BeEmpty();
        }

        [TestCase("\"C:\\Program Files (x86)\\GS Server\\GS.Server.exe\" /embedding", @"C:\Program Files (x86)\GS Server\GS.Server.exe")]
        [TestCase("C:\\GSS\\GS.Server.exe -Embedding", @"C:\GSS\GS.Server.exe")]
        [TestCase("C:\\Drivers\\Mount.EXE", @"C:\Drivers\Mount.EXE")]
        [TestCase("", null)]
        [TestCase(null, null)]
        public void The_program_of_an_ASCOM_local_server_is_read_from_its_registration(string value, string expected) {
            AscomDriverProgram.ParseLocalServer(value).Should().Be(expected);
        }

        [Test]
        public void An_unknown_driver_has_no_program() {
            AscomDriverProgram.ExeFor("ASCOM.DoesNotExist." + Guid.NewGuid().ToString("N")).Should().BeNull();
            AscomDriverProgram.ExeFor(null).Should().BeNull();
        }

        // ---------- PHD2's JSON-RPC server, faked on a local port ----------

        private static async Task<(List<string> Requests, int Port, Task Server)> FakePhd2Server(bool closeOnShutdown) {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var requests = new List<string>();
            var server = Task.Run(async () => {
                try {
                    // the first connection asks for the work; later ones are "is it still running?" checks
                    using var client = await listener.AcceptTcpClientAsync();
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
                    await writer.WriteLineAsync("{\"Event\":\"Version\",\"PHDVersion\":\"2.6.13\"}");
                    while (await reader.ReadLineAsync() is { } line) {
                        lock (requests) { requests.Add(line); }
                        var id = line.Substring(line.LastIndexOf("\"id\":", StringComparison.Ordinal) + 5).TrimEnd('}');
                        await writer.WriteLineAsync("{\"Event\":\"AppState\",\"State\":\"Stopped\"}");
                        await writer.WriteLineAsync($"{{\"jsonrpc\":\"2.0\",\"result\":0,\"id\":{id}}}");
                        if (line.Contains("\"shutdown\"") && closeOnShutdown) { break; }
                    }
                } finally {
                    listener.Stop(); // PHD2 has exited
                }
            });
            await Task.Yield();
            return (requests, port, server);
        }

        [Test]
        public async Task PHD2_is_asked_to_stop_disconnect_its_equipment_and_shut_down() {
            var (requests, port, server) = await FakePhd2Server(closeOnShutdown: true);
            var client = new Phd2Client(() => ("127.0.0.1", port), (d, t) => Task.Delay(10, t)) { RequestTimeout = TimeSpan.FromSeconds(5) };
            await client.ShutDown(closeApp: true, CancellationToken.None);
            await server;
            requests.Should().Equal(
                "{\"method\":\"stop_capture\",\"id\":1}",
                "{\"method\":\"set_connected\",\"params\":[false],\"id\":2}",
                "{\"method\":\"shutdown\",\"id\":3}");
            (await client.IsRunning(CancellationToken.None)).Should().BeFalse("PHD2 has closed");
        }

        [Test]
        public async Task Keeping_PHD2_open_only_disconnects_its_equipment() {
            var (requests, port, server) = await FakePhd2Server(closeOnShutdown: false);
            var client = new Phd2Client(() => ("127.0.0.1", port)) { RequestTimeout = TimeSpan.FromSeconds(5) };
            await client.ShutDown(closeApp: false, CancellationToken.None);
            await server;
            requests.Should().Equal("{\"method\":\"stop_capture\",\"id\":1}", "{\"method\":\"set_connected\",\"params\":[false],\"id\":2}");
        }

        [Test]
        public async Task Without_PHD2_running_nothing_fails() {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop(); // nothing listens on this port now
            var client = new Phd2Client(() => ("127.0.0.1", port));
            (await client.IsRunning(CancellationToken.None)).Should().BeFalse();
            await client.ShutDown(closeApp: true, CancellationToken.None);
        }
    }
}
