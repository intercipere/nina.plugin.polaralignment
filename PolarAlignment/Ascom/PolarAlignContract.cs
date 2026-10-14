using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace NINA.Plugins.PolarAlignment.Ascom {

    /// <summary>
    /// The PolarAlign Actions: a vendor-neutral contract for motorized polar alignment, carried over the
    /// standard ASCOM/Alpaca <c>Action()</c> and <c>SupportedActions</c> of any device type.
    /// Spec: https://wiki.openastrotech.com/en/Development/pa-actions
    ///
    /// Amounts are arcminutes of polar axis movement in local horizontal coordinates: +az increases the
    /// azimuth of the polar axis, +alt raises it. The device maps that to its motors.
    /// </summary>
    public static class PolarAlignContract {
        public const string InfoAction = "PolarAlign:Info";
        public const string MoveAction = "PolarAlign:Move";
        public const string StatusAction = "PolarAlign:Status";
        public const string StopAction = "PolarAlign:Stop";
        public const string HomeAction = "PolarAlign:Home";

        /// <summary>The major spec version this host understands.</summary>
        public const int SupportedMajorVersion = 0;

        public const string Azimuth = "az";
        public const string Altitude = "alt";

        /// <summary>A device offers the contract when it lists PolarAlign:Info; ASCOM action names are case-insensitive.</summary>
        public static bool IsOfferedBy(IEnumerable<string> supportedActions) =>
            supportedActions?.Any(a => string.Equals(a, InfoAction, StringComparison.OrdinalIgnoreCase)) == true;

        public static string MoveParameters(string axis, double arcmin) =>
            JsonSerializer.Serialize(new { axis, arcmin });

        /// <summary>The amount the device actually commanded, from the Move reply; the requested amount when the reply has none.</summary>
        public static double CommandedArcmin(string moveReply, double requested) {
            if (string.IsNullOrWhiteSpace(moveReply)) { return requested; }
            using var doc = Parse(moveReply, PolarAlignContract.MoveAction);
            return doc.RootElement.TryGetProperty("arcmin", out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetDouble()
                : requested;
        }

        /// <summary>Whether the given axis is moving, from the Status reply. An axis the reply does not list is not moving.</summary>
        public static bool IsMoving(string statusReply, string axis) {
            using var doc = Parse(statusReply, StatusAction);
            return doc.RootElement.TryGetProperty(axis, out var axisStatus)
                && axisStatus.ValueKind == JsonValueKind.Object
                && axisStatus.TryGetProperty("moving", out var moving)
                && moving.ValueKind == JsonValueKind.True;
        }

        internal static JsonDocument Parse(string json, string action) {
            try {
                var doc = JsonDocument.Parse(json ?? string.Empty);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) {
                    doc.Dispose();
                    throw new FormatException($"{action} did not reply with a JSON object: {json}");
                }
                return doc;
            } catch (JsonException ex) {
                throw new FormatException($"{action} replied with invalid JSON: {json}", ex);
            }
        }
    }

    /// <summary>One motorized axis, from PolarAlign:Info.</summary>
    public sealed class PolarAlignAxisInfo {
        public double MaxMove { get; init; }
        public double Resolution { get; init; }
        public bool BacklashHandled { get; init; }
    }

    /// <summary>What a device can do, from PolarAlign:Info. Unknown fields are ignored, absent optional ones take the spec's defaults.</summary>
    public sealed class PolarAlignInfo {
        public string SpecVersion { get; init; } = "";
        public IReadOnlyList<int> Tiers { get; init; } = Array.Empty<int>();
        public string DeviceName { get; init; } = "";
        public string DeviceVersion { get; init; } = "";
        public IReadOnlyDictionary<string, PolarAlignAxisInfo> Axes { get; init; } = new Dictionary<string, PolarAlignAxisInfo>();
        public bool SimultaneousMoves { get; init; }
        public bool MovesWhileTracking { get; init; } = true;
        public bool MovesWhileSlewing { get; init; }
        public bool CanStop { get; init; }
        public double SettleSeconds { get; init; }

        public int MajorVersion {
            get {
                var major = SpecVersion.Split('.')[0];
                return int.TryParse(major, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : -1;
            }
        }

        public bool IsSupported => MajorVersion == PolarAlignContract.SupportedMajorVersion && Tiers.Contains(1);

        public static PolarAlignInfo Parse(string json) {
            using var doc = PolarAlignContract.Parse(json, PolarAlignContract.InfoAction);
            var root = doc.RootElement;

            var axes = new Dictionary<string, PolarAlignAxisInfo>(StringComparer.Ordinal);
            if (root.TryGetProperty("axes", out var axesElement) && axesElement.ValueKind == JsonValueKind.Object) {
                foreach (var axis in axesElement.EnumerateObject()) {
                    if (axis.Value.ValueKind != JsonValueKind.Object) { continue; }
                    axes[axis.Name] = new PolarAlignAxisInfo {
                        MaxMove = Number(axis.Value, "maxMove", 0),
                        Resolution = Number(axis.Value, "resolution", 0),
                        BacklashHandled = Bool(axis.Value, "backlashHandled", false),
                    };
                }
            }

            var tiers = new List<int>();
            if (root.TryGetProperty("tiers", out var tiersElement) && tiersElement.ValueKind == JsonValueKind.Array) {
                foreach (var tier in tiersElement.EnumerateArray()) {
                    if (tier.ValueKind == JsonValueKind.Number && tier.TryGetInt32(out var t)) { tiers.Add(t); }
                }
            }

            return new PolarAlignInfo {
                SpecVersion = Text(root, "specVersion"),
                Tiers = tiers,
                DeviceName = Text(root, "deviceName"),
                DeviceVersion = Text(root, "deviceVersion"),
                Axes = axes,
                SimultaneousMoves = Bool(root, "simultaneousMoves", false),
                MovesWhileTracking = Bool(root, "movesWhileTracking", true),
                MovesWhileSlewing = Bool(root, "movesWhileSlewing", false),
                CanStop = Bool(root, "canStop", false),
                SettleSeconds = Number(root, "settleSeconds", 0),
            };
        }

        private static string Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : "";

        private static double Number(JsonElement element, string name, double fallback) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : fallback;

        private static bool Bool(JsonElement element, string name, bool fallback) =>
            element.TryGetProperty(name, out var value) && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
                ? value.GetBoolean()
                : fallback;
    }
}
