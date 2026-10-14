using NINA.Core.Utility;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Ascom {

    /// <summary>
    /// Drives the azimuth and altitude adjusters of an ASCOM/Alpaca device through the PolarAlign Actions
    /// (Tier 1). X is azimuth, Y is altitude; every position and move is in arcminutes of polar axis movement,
    /// so the gear ratio is fixed at 1. The device owns the conversion to motor steps.
    /// </summary>
    public sealed class AscomPolarAlignmentSystem : IPolarAlignmentSystem {
        private readonly IPolarAlignActionDevice device;
        private readonly TimeSpan pollInterval;

        // Positions are the sum of the moves made through this object: the contract does not require the
        // device to report one.
        private float xPosition;
        private float yPosition;

        public AscomPolarAlignmentSystem(IPolarAlignActionDevice device) : this(device, TimeSpan.FromMilliseconds(250)) { }

        internal AscomPolarAlignmentSystem(IPolarAlignActionDevice device, TimeSpan pollInterval) {
            this.device = device ?? throw new ArgumentNullException(nameof(device));
            this.pollInterval = pollInterval;

            if (!device.Connected) {
                throw new InvalidOperationException($"{device.Name} is not connected. Connect the mount in N.I.N.A. first.");
            }
            if (!PolarAlignContract.IsOfferedBy(device.SupportedActions)) {
                throw new NotSupportedException($"{device.Name} does not offer the PolarAlign actions ({PolarAlignContract.InfoAction} is not in its SupportedActions).");
            }

            Info = PolarAlignInfo.Parse(device.Action(PolarAlignContract.InfoAction, string.Empty));
            if (!Info.IsSupported) {
                throw new NotSupportedException($"{device.Name} implements PolarAlign actions version {Info.SpecVersion} (tiers {string.Join(",", Info.Tiers)}); this plugin understands version {PolarAlignContract.SupportedMajorVersion}.x, tier 1.");
            }
            if (Info.Axes.Count == 0) {
                throw new NotSupportedException($"{device.Name} reports no motorized polar alignment axis.");
            }

            Status = "Idle";
            Logger.Info($"PolarAlign actions: {Describe()}");
        }

        public PolarAlignInfo Info { get; }

        // Closed once disposed: the mount stays connected in N.I.N.A., but the view model only builds a
        // new system on Connect when the old one reports itself disconnected.
        private bool disposed;

        public bool Connected => !disposed && device.Connected;
        public string Status { get; private set; }

        public float XPosition1 => xPosition;
        public float YPosition1 => yPosition;
        public float ZPosition1 => 0;

        // Moves are in arcminutes already.
        public float XGearRatio { get => 1; set { } }
        public float YGearRatio { get => 1; set { } }
        public float ZGearRatio { get => 1; set { } }

        public LastDirection XLastDirection { get; private set; } = LastDirection.Positive;
        public LastDirection YLastDirection { get; private set; } = LastDirection.Positive;
        public LastDirection ZLastDirection => LastDirection.Positive;

        public bool HasAxis(Axis axis) => axis != Axis.ZAxis && Info.Axes.ContainsKey(AxisName(axis));

        public bool BacklashHandled(Axis axis) =>
            axis != Axis.ZAxis && Info.Axes.TryGetValue(AxisName(axis), out var info) && info.BacklashHandled;

        public string Describe() {
            var axes = string.Join(", ", Info.Axes.Keys);
            var name = string.IsNullOrWhiteSpace(Info.DeviceName) ? device.Name : Info.DeviceName;
            var stop = Info.CanStop ? "can stop" : "cannot stop";
            return $"{name} {Info.DeviceVersion}, spec {Info.SpecVersion}, axes: {axes}, {stop}".Replace("  ", " ");
        }

        public async Task MoveRelative(Axis axis, int speed, float position, CancellationToken token) {
            var name = AxisName(axis);
            if (!Info.Axes.TryGetValue(name, out var axisInfo)) {
                throw new NotSupportedException($"{device.Name} has no motorized {name} axis.");
            }
            if (position == 0) { return; }

            if (axis == Axis.XAxis) {
                XLastDirection = position > 0 ? LastDirection.Positive : LastDirection.Negative;
            } else {
                YLastDirection = position > 0 ? LastDirection.Positive : LastDirection.Negative;
            }

            // Larger corrections than the device takes in one move are split.
            var remaining = (double)position;
            var maxMove = axisInfo.MaxMove > 0 ? axisInfo.MaxMove : Math.Abs(remaining);
            while (Math.Abs(remaining) > 1e-9) {
                token.ThrowIfCancellationRequested();
                var step = Math.Sign(remaining) * Math.Min(Math.Abs(remaining), maxMove);
                var parameters = PolarAlignContract.MoveParameters(name, step);
                Logger.Info($"PolarAlign: {PolarAlignContract.MoveAction} {parameters}");
                var reply = device.Action(PolarAlignContract.MoveAction, parameters);
                var commanded = PolarAlignContract.CommandedArcmin(reply, step);
                if (axis == Axis.XAxis) {
                    xPosition += (float)commanded;
                } else {
                    yPosition += (float)commanded;
                }
                await WaitUntilStopped(name, commanded, token).ConfigureAwait(false);
                remaining -= step;
            }
        }

        public Task MoveAbsolute(Axis axis, int speed, float position, CancellationToken token) {
            var current = axis switch {
                Axis.XAxis => xPosition,
                Axis.YAxis => yPosition,
                _ => throw new NotSupportedException("The PolarAlign actions have no Z axis."),
            };
            return MoveRelative(axis, speed, position - current, token);
        }

        public Task RefreshStatus(CancellationToken token) {
            try {
                var reply = device.Action(PolarAlignContract.StatusAction, string.Empty);
                var moving = false;
                foreach (var name in Info.Axes.Keys) {
                    moving |= PolarAlignContract.IsMoving(reply, name);
                }
                Status = moving ? "Run" : "Idle";
            } catch (Exception ex) {
                Logger.Debug($"PolarAlign: reading the status failed: {ex.Message}");
            }
            return Task.CompletedTask;
        }

        /// <summary>Stops both axes when the device can; says whether the stop was sent.</summary>
        public bool TryStop() {
            if (!Info.CanStop) {
                Logger.Warning($"PolarAlign: {device.Name} cannot stop a move (canStop is false)");
                return false;
            }
            try {
                device.Action(PolarAlignContract.StopAction, string.Empty);
                Logger.Info("PolarAlign: stop sent");
                return true;
            } catch (Exception ex) {
                Logger.Error($"PolarAlign: stop failed: {ex.Message}");
                return false;
            }
        }

        private async Task WaitUntilStopped(string axis, double arcmin, CancellationToken token) {
            // The contract carries no speed, so the timeout is generous: a minute plus 5 s per arcminute.
            var timeout = TimeSpan.FromSeconds(60 + 5 * Math.Abs(arcmin));
            var stopwatch = Stopwatch.StartNew();
            try {
                while (true) {
                    await Task.Delay(pollInterval, token).ConfigureAwait(false);
                    var reply = device.Action(PolarAlignContract.StatusAction, string.Empty);
                    if (!PolarAlignContract.IsMoving(reply, axis)) {
                        Status = "Idle";
                        return;
                    }
                    Status = "Run";
                    if (stopwatch.Elapsed > timeout) {
                        TryStop();
                        throw new TimeoutException($"The {axis} axis was still moving after {timeout.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)} s");
                    }
                }
            } catch (OperationCanceledException) {
                TryStop();
                throw;
            }
        }

        private static string AxisName(Axis axis) => axis switch {
            Axis.XAxis => PolarAlignContract.Azimuth,
            Axis.YAxis => PolarAlignContract.Altitude,
            _ => throw new NotSupportedException("The PolarAlign actions have no Z axis."),
        };

        // The connection belongs to N.I.N.A.'s mount and stays open; a move still running is stopped.
        public void Dispose() {
            if (disposed) { return; }
            if (Status == "Run") {
                TryStop();
            }
            disposed = true;
        }
    }
}
