using FluentAssertions;
using NINA.ObservatoryPlanner.Core;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.ObservatoryPlanner.Tests {

    /// <summary>
    /// Device recovery: the devices that stay connected (safety monitor, switch, dome, weather) are reconnected when their
    /// Alpaca server stopped answering and NINA disconnected them (the rig on 24 Oct 2026 at 06:58).
    /// </summary>
    [TestFixture]
    public class DeviceRecoveryTests {

        private sealed class RecordingLog : IPlannerLog {
            public readonly List<string> Infos = new();
            public readonly List<(PlannerPhase phase, string message)> Phases = new();
            public readonly List<string> Statuses = new();
            public string Problem;
            public void Phase(PlannerPhase phase, string message, PlannerTarget target = null) { lock (Phases) { Phases.Add((phase, message)); } }
            public void Info(string message) { lock (Infos) { Infos.Add(message); } }
            public void Status(string message) { lock (Statuses) { Statuses.Add(message); } }
            public void DeviceProblem(string message) => Problem = message;
        }

        /// <summary>Devices that drop and come back: a device listed in <see cref="Missing"/> reconnects on the given try.</summary>
        private sealed class DroppingDevices {
            public readonly Dictionary<string, int> Missing = new();
            public readonly List<DateTime> Tries = new();
            private readonly Func<DateTime> now;
            public DroppingDevices(Func<DateTime> now) { this.now = now; }
            public bool IsMissing(string device) => Missing.ContainsKey(device);

            public Task<KeptDevicesCheck> Reconnect(CancellationToken token) {
                Tries.Add(now());
                var back = new List<string>();
                foreach (var d in Missing.Keys.ToList()) {
                    if (--Missing[d] <= 0) { Missing.Remove(d); back.Add(d); }
                }
                return Task.FromResult(new KeptDevicesCheck(back, Missing.Keys.ToList()));
            }
        }

        [Test]
        public async Task A_dropped_device_is_logged_once_tried_every_minute_and_reported_when_back() {
            var now = TestData.Night(7);
            var log = new RecordingLog();
            var devices = new DroppingDevices(() => now);
            devices.Missing["Safety Monitor"] = 5; // back on the 5th try
            var recovery = new DeviceRecovery(new PlannerOptions(), log, () => now);

            for (var i = 0; i < 10 * 60 / 2; i++) { // 10 minutes of 2 s polls
                await recovery.Check(devices.Reconnect, CancellationToken.None);
                now += TimeSpan.FromSeconds(2);
            }

            devices.Tries.Should().HaveCount(10, "one try per minute, not every poll");
            log.Infos.Count(i => i.Contains("Safety Monitor is not connected")).Should().Be(1, "a device that keeps failing is logged once");
            log.Infos.Should().Contain("Device recovery: Safety Monitor reconnected after 4:00");
            log.Problem.Should().BeNull();
        }

        [Test]
        public async Task A_device_missing_too_long_shows_a_warning_until_it_is_back() {
            var now = TestData.Night(7);
            var log = new RecordingLog();
            var devices = new DroppingDevices(() => now);
            devices.Missing["Dome"] = 20;
            devices.Missing["Weather"] = 20;
            var recovery = new DeviceRecovery(new PlannerOptions { DeviceMissingWarnMinutes = 15 }, log, () => now);

            for (var minute = 0; minute <= 15; minute++) {
                await recovery.Check(devices.Reconnect, CancellationToken.None);
                if (minute < 15) { log.Problem.Should().BeNull($"no warning yet at {minute} min"); }
                now += TimeSpan.FromMinutes(1);
            }
            log.Problem.Should().StartWith("Not connected for more than 15 min: Dome, Weather");

            while (devices.Missing.Count > 0) {
                await recovery.Check(devices.Reconnect, CancellationToken.None);
                now += TimeSpan.FromMinutes(1);
            }
            log.Problem.Should().BeNull("the warning goes when the devices are back");
            log.Infos.Should().Contain(i => i.StartsWith("Device recovery: Dome reconnected after"));
        }

        [Test]
        public async Task Immediately_does_not_wait_for_the_next_minute_and_off_never_reconnects() {
            var now = TestData.Night(7);
            var devices = new DroppingDevices(() => now);
            devices.Missing["Dome"] = 2;
            var recovery = new DeviceRecovery(new PlannerOptions(), new RecordingLog(), () => now);
            await recovery.Check(devices.Reconnect, CancellationToken.None);
            await recovery.Check(devices.Reconnect, CancellationToken.None, immediately: true);
            devices.Tries.Should().HaveCount(2, "before closing the roof the dome is tried right away");

            var off = new DeviceRecovery(new PlannerOptions { DeviceRecovery = false }, new RecordingLog(), () => now);
            await off.Check(devices.Reconnect, CancellationToken.None, immediately: true);
            devices.Tries.Should().HaveCount(2, "device recovery is off");
        }

        /// <summary>The engine's hardware with devices that can drop; the safety monitor reads as not connected while missing.</summary>
        private sealed class DroppingHardware : IPlannerHardware {
            private readonly FakeClock clock;
            public readonly DroppingDevices Devices;
            public readonly List<string> Calls = new();
            public DroppingHardware(FakeClock clock) { this.clock = clock; Devices = new DroppingDevices(() => clock.Now); }

            public Task ConnectKeptDevices(CancellationToken token) { Calls.Add("connect"); return Task.CompletedTask; }

            public async Task<KeptDevicesCheck> ReconnectKeptDevices(CancellationToken token) {
                Calls.Add($"reconnect {string.Join(",", Devices.Missing.Keys)}");
                return await Devices.Reconnect(token);
            }

            public Task RunStage(StageKind stage, CancellationToken token) {
                Calls.Add($"stage {(int)stage}");
                clock.Advance(TimeSpan.FromMinutes(1));
                return Task.CompletedTask;
            }

            public async Task<int> RunTarget(PlannerTarget target, Func<PlannerExposure, bool> canStartFrame, CancellationToken token) {
                Calls.Add($"target {target.Name}");
                var frames = 0;
                while (ExposurePlanner.Next(target, null) is { } e && canStartFrame(e)) {
                    for (int i = 0; i < 5; i++) { await Task.Delay(1, token); }
                    clock.Advance(TimeSpan.FromSeconds(e.ExposureTime));
                    token.ThrowIfCancellationRequested();
                    e.Done++;
                    frames++;
                }
                return frames;
            }

            public Task RunGapSteps(IReadOnlyList<GapStep> steps, CancellationToken token) => Task.CompletedTask;
        }

        private sealed class MonitorOf : ISafetySource {
            private readonly DroppingHardware hw;
            private readonly Func<bool> safe;
            public MonitorOf(DroppingHardware hw, Func<bool> safe) { this.hw = hw; this.safe = safe; }
            public SafetyState Read() => hw.Devices.IsMissing("Safety Monitor") ? new SafetyState(false, false) : new SafetyState(true, safe());
        }

        private static async Task RunUntil(PlannerEngine engine, Func<bool> done) {
            using var cts = new CancellationTokenSource();
            var run = engine.RunAsync(cts.Token);
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!done() && !run.IsCompleted && DateTime.UtcNow < deadline) { await Task.Delay(5); }
            cts.Cancel();
            try { await run; } catch (OperationCanceledException) { }
        }

        [Test]
        public async Task A_safety_monitor_that_dropped_while_waiting_for_safe_is_reconnected_and_the_night_starts() {
            var clock = new FakeClock(TestData.Night(21));
            var hw = new DroppingHardware(clock);
            hw.Devices.Missing["Safety Monitor"] = 3; // back on the 3rd try, about 2 min later
            var log = new RecordingLog();
            var t1 = TestData.Circumpolar("T1", frames: 2, exposure: 60);
            var engine = new PlannerEngine(new PlannerOptions(), TestData.Selector(), () => new List<PlannerTarget> { t1 }, hw,
                                           new MonitorOf(hw, () => true), clock, log) { WatchDelay = (d, t) => Task.Delay(1, t) };

            await RunUntil(engine, () => hw.Calls.Contains("target T1"));

            hw.Calls.Should().Contain("target T1", "without device recovery it would wait for safe forever");
            hw.Calls.IndexOf("stage 1").Should().BeGreaterThan(hw.Calls.LastIndexOf("reconnect Safety Monitor"), "1 Begin only after the monitor is back");
            log.Statuses.Should().Contain("Waiting for safe: the safety monitor is not connected (reconnecting every 1 min)");
            log.Infos.Should().Contain(i => i.StartsWith("Device recovery: Safety Monitor reconnected after"));
        }

        [Test]
        public async Task Without_device_recovery_a_dropped_monitor_keeps_the_planner_waiting() {
            var clock = new FakeClock(TestData.Night(21));
            var hw = new DroppingHardware(clock);
            hw.Devices.Missing["Safety Monitor"] = 3;
            var log = new RecordingLog();
            var t1 = TestData.Circumpolar("T1", frames: 2, exposure: 60);
            var engine = new PlannerEngine(new PlannerOptions { DeviceRecovery = false }, TestData.Selector(), () => new List<PlannerTarget> { t1 }, hw,
                                           new MonitorOf(hw, () => true), clock, log);

            await RunUntil(engine, () => clock.Now > TestData.Night(22));

            hw.Calls.Should().NotContain(c => c.StartsWith("reconnect") || c == "stage 1");
            log.Statuses.Should().Contain("Waiting for safe: the safety monitor is not connected (device recovery is off)");
        }

        [Test]
        public async Task The_dome_is_reconnected_right_away_before_4_End_closes_the_roof() {
            var clock = new FakeClock(TestData.Night(21));
            var hw = new DroppingHardware(clock);
            var log = new RecordingLog();
            var t1 = TestData.Circumpolar("T1", frames: 3, exposure: 60);
            var engine = new PlannerEngine(new PlannerOptions { RunMode = RunMode.WithoutSafety }, TestData.Selector(), () => new List<PlannerTarget> { t1 }, hw,
                                           new MonitorOf(hw, () => true), clock, log);
            hw.Calls.Clear();
            var run = engine.RunAsync(CancellationToken.None);
            // the dome's server stops answering during imaging
            while (!hw.Calls.Contains("target T1")) { await Task.Delay(1); }
            hw.Devices.Missing["Dome"] = 1;
            await run;

            var end = hw.Calls.IndexOf("stage 4");
            end.Should().BeGreaterThan(0);
            hw.Calls[end - 1].Should().Be("reconnect Dome", "4 End needs the dome to close the roof");
            log.Infos.Should().Contain(i => i.StartsWith("Device recovery: Dome reconnected after") || i == "Device recovery: Dome had dropped; reconnected");
        }
    }
}
