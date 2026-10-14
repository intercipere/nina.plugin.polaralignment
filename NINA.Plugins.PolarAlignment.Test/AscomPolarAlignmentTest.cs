using FluentAssertions;
using NINA.Plugins.PolarAlignment.Ascom;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// The ASCOM system against a fake device that implements the PolarAlign actions the way the
    /// spec describes them: Info, asynchronous Move, Status polled until the axis stops, Stop.
    /// </summary>
    public class AscomPolarAlignmentTest {

        private const string OatInfo =
            "{\"specVersion\":\"0.1\",\"tiers\":[1],\"deviceName\":\"OpenAstroTracker\",\"deviceVersion\":\"V1.13.23\"," +
            "\"axes\":{\"az\":{\"maxMove\":120,\"backlashHandled\":false},\"alt\":{\"maxMove\":120,\"backlashHandled\":false}}," +
            "\"simultaneousMoves\":true,\"movesWhileTracking\":true,\"movesWhileSlewing\":false,\"canStop\":true,\"settleSeconds\":2}";

        /// <summary>A device that records every action; each move keeps its axis "moving" for a few polls.</summary>
        internal sealed class FakeDevice : IPolarAlignActionDevice {
            public readonly List<(string name, string parameters)> Calls = new();
            public string InfoJson = OatInfo;
            public bool IsConnected = true;
            public string[] Actions = { "Telescope:getFirmwareVer", "polaralign:info", "PolarAlign:Move", "PolarAlign:Status", "PolarAlign:Stop" };
            public int PollsWhileMoving = 2;
            public bool NeverStops;
            private readonly Dictionary<string, int> movingPolls = new();

            public string Name => "Fake mount";
            public bool Connected => IsConnected;
            public IReadOnlyCollection<string> SupportedActions => Actions;

            public IEnumerable<(string axis, double arcmin)> Moves =>
                Calls.Where(c => c.name == PolarAlignContract.MoveAction).Select(c => {
                    using var doc = JsonDocument.Parse(c.parameters);
                    return (doc.RootElement.GetProperty("axis").GetString(), doc.RootElement.GetProperty("arcmin").GetDouble());
                }).ToList();

            public string Action(string actionName, string actionParameters) {
                Calls.Add((actionName, actionParameters));
                switch (actionName) {
                    case PolarAlignContract.InfoAction:
                        return InfoJson;
                    case PolarAlignContract.MoveAction: {
                            using var doc = JsonDocument.Parse(actionParameters);
                            var axis = doc.RootElement.GetProperty("axis").GetString();
                            movingPolls[axis] = NeverStops ? int.MaxValue : PollsWhileMoving;
                            return "{\"arcmin\":" + doc.RootElement.GetProperty("arcmin").GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
                        }
                    case PolarAlignContract.StatusAction: {
                            var parts = new List<string>();
                            foreach (var axis in new[] { "az", "alt" }) {
                                movingPolls.TryGetValue(axis, out var left);
                                parts.Add($"\"{axis}\":{{\"moving\":{(left > 0 ? "true" : "false")}}}");
                                if (left > 0 && left != int.MaxValue) { movingPolls[axis] = left - 1; }
                            }
                            return "{" + string.Join(",", parts) + "}";
                        }
                    case PolarAlignContract.StopAction:
                        movingPolls.Clear();
                        return "";
                    default:
                        throw new InvalidOperationException($"unexpected action {actionName}");
                }
            }
        }

        internal sealed class AscomTestVm : AscomPolarAlignmentVM {
            public AscomTestVm(Func<IPolarAlignActionDevice> createDevice) : base(null, createDevice) { }

            public IPolarAlignmentSystem Hardware {
                get => upa;
                set => upa = value;
            }

            public float Backlash(Axis axis) => GetBacklashCompensation(axis);
        }

        private static AscomPolarAlignmentSystem Create(FakeDevice device) => new AscomPolarAlignmentSystem(device, TimeSpan.FromMilliseconds(1));

        [Test]
        public void Info_IsParsed_AndUnknownFieldsAreIgnored() {
            var info = PolarAlignInfo.Parse(OatInfo.Replace("\"canStop\":true", "\"canStop\":true,\"future\":{\"x\":[1,2]}"));

            info.SpecVersion.Should().Be("0.1");
            info.MajorVersion.Should().Be(0);
            info.IsSupported.Should().BeTrue();
            info.DeviceName.Should().Be("OpenAstroTracker");
            info.Axes.Keys.Should().BeEquivalentTo("az", "alt");
            info.Axes["az"].MaxMove.Should().Be(120);
            info.CanStop.Should().BeTrue();
            info.SettleSeconds.Should().Be(2);
        }

        [Test]
        public void Info_AbsentOptionalFields_TakeTheSpecDefaults() {
            var info = PolarAlignInfo.Parse("{\"specVersion\":\"0.1\",\"tiers\":[1],\"axes\":{\"alt\":{\"maxMove\":30}}}");

            info.CanStop.Should().BeFalse();
            info.SimultaneousMoves.Should().BeFalse();
            info.MovesWhileTracking.Should().BeTrue();
            info.MovesWhileSlewing.Should().BeFalse();
            info.SettleSeconds.Should().Be(0);
            info.Axes["alt"].BacklashHandled.Should().BeFalse();
            info.Axes.Should().NotContainKey("az");
        }

        [TestCase("{\"specVersion\":\"1.0\",\"tiers\":[1],\"axes\":{\"az\":{\"maxMove\":1}}}")]
        [TestCase("{\"specVersion\":\"0.1\",\"tiers\":[2],\"axes\":{\"az\":{\"maxMove\":1}}}")]
        public void UnknownMajorVersionOrNoTier1_IsRefused(string infoJson) {
            var device = new FakeDevice { InfoJson = infoJson };
            Action create = () => Create(device);
            create.Should().Throw<NotSupportedException>();
        }

        [Test]
        public void ADeviceWithoutTheActions_IsRefused_WithTheReason() {
            var device = new FakeDevice { Actions = new[] { "Telescope:getFirmwareVer" } };
            Action create = () => Create(device);
            create.Should().Throw<NotSupportedException>().WithMessage("*PolarAlign:Info*SupportedActions*");
        }

        [Test]
        public void ADisconnectedMount_IsRefused() {
            var device = new FakeDevice { IsConnected = false };
            Action create = () => Create(device);
            create.Should().Throw<InvalidOperationException>().WithMessage("*not connected*");
        }

        [Test]
        public void ADeviceWithoutAxes_IsRefused() {
            var device = new FakeDevice { InfoJson = "{\"specVersion\":\"0.1\",\"tiers\":[1],\"axes\":{}}" };
            Action create = () => Create(device);
            create.Should().Throw<NotSupportedException>().WithMessage("*no motorized*");
        }

        [Test]
        public void SupportedActions_AreMatchedCaseInsensitively() {
            PolarAlignContract.IsOfferedBy(new[] { "POLARALIGN:INFO" }).Should().BeTrue();
            PolarAlignContract.IsOfferedBy(new[] { "PolarAlign:Move" }).Should().BeFalse();
            PolarAlignContract.IsOfferedBy(null).Should().BeFalse();
        }

        [Test]
        public async Task MoveRelative_SendsArcminutes_AndWaitsUntilTheAxisStops() {
            var device = new FakeDevice { PollsWhileMoving = 3 };
            var system = Create(device);

            await system.MoveRelative(Axis.XAxis, 0, -2.5f, CancellationToken.None);

            device.Moves.Should().Equal(("az", -2.5));
            device.Calls.Count(c => c.name == PolarAlignContract.StatusAction).Should().Be(4, "three polls while moving and one that sees it stopped");
            system.XPosition1.Should().Be(-2.5f);
            system.XLastDirection.Should().Be(LastDirection.Negative);
            system.Status.Should().Be("Idle");
        }

        [Test]
        public async Task AltitudeMovesGoToTheAltAxis() {
            var device = new FakeDevice();
            var system = Create(device);

            await system.MoveRelative(Axis.YAxis, 0, 1.25f, CancellationToken.None);

            device.Moves.Should().Equal(("alt", 1.25));
            system.YPosition1.Should().Be(1.25f);
            system.YLastDirection.Should().Be(LastDirection.Positive);
        }

        [Test]
        public async Task MovesLargerThanMaxMove_AreSplit() {
            var device = new FakeDevice { InfoJson = OatInfo.Replace("\"az\":{\"maxMove\":120", "\"az\":{\"maxMove\":10") };
            var system = Create(device);

            await system.MoveRelative(Axis.XAxis, 0, 25f, CancellationToken.None);

            device.Moves.Should().Equal(("az", 10.0), ("az", 10.0), ("az", 5.0));
            system.XPosition1.Should().Be(25f);
        }

        [Test]
        public async Task MoveAbsolute_MovesByTheDifference() {
            var device = new FakeDevice();
            var system = Create(device);
            await system.MoveRelative(Axis.YAxis, 0, 3f, CancellationToken.None);

            await system.MoveAbsolute(Axis.YAxis, 0, 1f, CancellationToken.None);

            device.Moves.Should().Equal(("alt", 3.0), ("alt", -2.0));
            system.YPosition1.Should().Be(1f);
        }

        [Test]
        public async Task AZeroMove_SendsNothing() {
            var device = new FakeDevice();
            var system = Create(device);

            await system.MoveRelative(Axis.XAxis, 0, 0f, CancellationToken.None);

            device.Moves.Should().BeEmpty();
        }

        [Test]
        public async Task AMoveOnAMissingAxis_IsRefused() {
            var device = new FakeDevice { InfoJson = "{\"specVersion\":\"0.1\",\"tiers\":[1],\"axes\":{\"alt\":{\"maxMove\":30}}}" };
            var system = Create(device);

            Func<Task> move = () => system.MoveRelative(Axis.XAxis, 0, 1f, CancellationToken.None);

            await move.Should().ThrowAsync<NotSupportedException>();
            system.HasAxis(Axis.XAxis).Should().BeFalse();
            system.HasAxis(Axis.YAxis).Should().BeTrue();
        }

        [Test]
        public async Task CancellingAMove_StopsTheDevice() {
            var device = new FakeDevice { NeverStops = true };
            var system = Create(device);
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

            Func<Task> move = () => system.MoveRelative(Axis.XAxis, 0, 5f, cts.Token);

            await move.Should().ThrowAsync<OperationCanceledException>();
            device.Calls.Should().Contain(c => c.name == PolarAlignContract.StopAction);
        }

        [Test]
        public void TryStop_IsNotSent_WhenTheDeviceCannotStop() {
            var device = new FakeDevice { InfoJson = OatInfo.Replace("\"canStop\":true", "\"canStop\":false") };
            var system = Create(device);

            system.TryStop().Should().BeFalse();
            device.Calls.Should().NotContain(c => c.name == PolarAlignContract.StopAction);
        }

        [Test]
        public async Task RefreshStatus_ReportsRunWhileAnAxisMoves() {
            var device = new FakeDevice { NeverStops = true };
            var system = Create(device);
            device.Action(PolarAlignContract.MoveAction, PolarAlignContract.MoveParameters("alt", 1));

            await system.RefreshStatus(CancellationToken.None);

            system.Status.Should().Be("Run");
        }

        [Test]
        public void ADisposedSystem_ReportsDisconnected_WhileTheMountStaysConnected() {
            // The view model's Connect() only builds a new system when the old one reports itself
            // disconnected; TPPA disposes the system at the end of every run and on Disconnect.
            var device = new FakeDevice();
            var system = Create(device);
            system.Connected.Should().BeTrue();

            system.Dispose();
            system.Dispose();

            system.Connected.Should().BeFalse();
            device.Connected.Should().BeTrue("the mount connection belongs to N.I.N.A.");
            device.Calls.Should().NotContain(c => c.name == PolarAlignContract.StopAction, "nothing was moving");
        }

        [Test]
        public async Task DisposingWhileAMoveRuns_StopsIt() {
            var device = new FakeDevice { NeverStops = true };
            var system = Create(device);
            device.Action(PolarAlignContract.MoveAction, PolarAlignContract.MoveParameters("az", 5));
            await system.RefreshStatus(CancellationToken.None);

            system.Dispose();

            device.Calls.Should().Contain(c => c.name == PolarAlignContract.StopAction);
        }

        [Test]
        public void MoveParameters_AreTheSpecsJson() {
            PolarAlignContract.MoveParameters("az", -1.25).Should().Be("{\"axis\":\"az\",\"arcmin\":-1.25}");
        }

        [Test]
        public void CommandedArcmin_FallsBackToTheRequest_WhenTheReplyHasNone() {
            PolarAlignContract.CommandedArcmin("{\"arcmin\":0.5}", 0.52).Should().Be(0.5);
            PolarAlignContract.CommandedArcmin("", 0.52).Should().Be(0.52);
            PolarAlignContract.CommandedArcmin("{}", 0.52).Should().Be(0.52);
        }

        [Test]
        public async Task TheViewModel_NudgesInArcminutes_AndHonoursReverse() {
            var device = new FakeDevice();
            var vm = new AscomTestVm(() => device) { Hardware = Create(device) };
            var reverse = Properties.Settings.Default.AscomReverseAltitude;
            var backlash = Properties.Settings.Default.AscomYBacklashCompensation;
            try {
                Properties.Settings.Default.AscomReverseAltitude = true;
                Properties.Settings.Default.AscomYBacklashCompensation = 0f;

                (await vm.TryNudgeY(1f, CancellationToken.None)).Should().BeTrue();

                device.Moves.Should().Equal(("alt", -1.0));
            } finally {
                Properties.Settings.Default.AscomReverseAltitude = reverse;
                Properties.Settings.Default.AscomYBacklashCompensation = backlash;
            }
        }

        [Test]
        public void Backlash_IsLeftToTheDevice_WhenItHandlesItsOwn() {
            var handled = new FakeDevice { InfoJson = OatInfo.Replace("\"backlashHandled\":false", "\"backlashHandled\":true") };
            var plain = new FakeDevice();
            var backlash = Properties.Settings.Default.AscomXBacklashCompensation;
            try {
                Properties.Settings.Default.AscomXBacklashCompensation = 3f;

                new AscomTestVm(() => handled) { Hardware = Create(handled) }.Backlash(Axis.XAxis).Should().Be(0f);
                new AscomTestVm(() => plain) { Hardware = Create(plain) }.Backlash(Axis.XAxis).Should().Be(3f);
            } finally {
                Properties.Settings.Default.AscomXBacklashCompensation = backlash;
            }
        }

        [Test]
        public void TheSettleTime_IsAtLeastWhatTheDeviceSuggests() {
            var device = new FakeDevice();
            var vm = new AscomTestVm(() => device) { Hardware = Create(device) };
            var settle = Properties.Settings.Default.AscomAutomatedAdjustmentSettleTime;
            try {
                Properties.Settings.Default.AscomAutomatedAdjustmentSettleTime = 0.5;
                vm.AutomatedAdjustmentSettleTime.Should().Be(2);
                Properties.Settings.Default.AscomAutomatedAdjustmentSettleTime = 5;
                vm.AutomatedAdjustmentSettleTime.Should().Be(5);
            } finally {
                Properties.Settings.Default.AscomAutomatedAdjustmentSettleTime = settle;
            }
        }
    }
}
