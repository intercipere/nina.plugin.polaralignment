using System;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment {
    public enum PolarAlignmentSystemType {
        None,
        UPAS,
        OAPA,
        /// <summary>The mount's own adjusters, through the PolarAlign actions over its ASCOM/Alpaca connection.</summary>
        ASCOM
    }

    public enum Axis {
        XAxis,
        YAxis,
        ZAxis
    }

    public enum LastDirection {
        Negative,
        Positive
    }

    public interface IPolarAlignmentSystem : IDisposable {
        bool Connected { get; }
        string Status { get; }

        float XPosition1 { get; }
        float YPosition1 { get; }
        float ZPosition1 { get; }

        float XGearRatio { get; set; }
        float YGearRatio { get; set; }
        float ZGearRatio { get; set; }

        LastDirection XLastDirection { get; }
        LastDirection YLastDirection { get; }
        LastDirection ZLastDirection { get; }

        Task MoveRelative(Axis axis, int speed, float position, CancellationToken token);
        Task MoveAbsolute(Axis axis, int speed, float position, CancellationToken token);
        Task RefreshStatus(CancellationToken token);
    }
}
