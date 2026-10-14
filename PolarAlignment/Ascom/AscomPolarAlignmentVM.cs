using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using System;

namespace NINA.Plugins.PolarAlignment.Ascom {

    /// <summary>
    /// The azimuth and altitude adjusters of the mount connected in N.I.N.A., driven through the PolarAlign
    /// Actions over its ASCOM/Alpaca connection: no second port and no gear ratio, since every move is in
    /// arcminutes. TPPA's own correction loop decides the moves, as for UPAS.
    /// </summary>
    public partial class AscomPolarAlignmentVM : UniversalPolarAlignmentBaseVM {
        private readonly Func<IPolarAlignActionDevice> createDevice;

        public AscomPolarAlignmentVM(IProfileService profileService, ITelescopeMediator telescopeMediator)
            : this(profileService, () => new ConnectedMountActionDevice(telescopeMediator)) { }

        internal AscomPolarAlignmentVM(IProfileService profileService, Func<IPolarAlignActionDevice> createDevice) : base(profileService) {
            this.createDevice = createDevice ?? throw new ArgumentNullException(nameof(createDevice));
        }

        protected override string SystemName => "Mount (PolarAlign actions)";

        /// <summary>What the connected device reported, or why connecting failed.</summary>
        [ObservableProperty]
        private string deviceDescription = "Not connected";

        [ObservableProperty]
        private bool hasAzimuth;

        [ObservableProperty]
        private bool hasAltitude;

        [ObservableProperty]
        private bool canStop;

        protected override IPolarAlignmentSystem CreateSystem() {
            try {
                var system = new AscomPolarAlignmentSystem(createDevice());
                _ = RunOnUi(() => {
                    DeviceDescription = system.Describe();
                    HasAzimuth = system.HasAxis(Axis.XAxis);
                    HasAltitude = system.HasAxis(Axis.YAxis);
                    CanStop = system.Info.CanStop;
                    RaisePropertyChanged(nameof(AutomatedAdjustmentSettleTime));
                });
                return system;
            } catch (Exception ex) {
                // The base reports only "Unable to connect"; the reason is shown in the panel.
                _ = RunOnUi(() => DeviceDescription = ex.Message);
                throw;
            }
        }

        private AscomPolarAlignmentSystem AscomSystem => upa as AscomPolarAlignmentSystem;

        [RelayCommand]
        private void Stop() {
            AscomSystem?.TryStop();
        }

        public override bool DoAutomatedAdjustments {
            get => Properties.Settings.Default.AscomDoAutomatedAdjustments;
            set {
                Properties.Settings.Default.AscomDoAutomatedAdjustments = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        /// <summary>At least the settle time the device suggests (settleSeconds in PolarAlign:Info).</summary>
        public override double AutomatedAdjustmentSettleTime {
            get => Math.Max(Properties.Settings.Default.AscomAutomatedAdjustmentSettleTime, AscomSystem?.Info.SettleSeconds ?? 0);
            set {
                Properties.Settings.Default.AscomAutomatedAdjustmentSettleTime = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        // Every move is in arcminutes: the device owns gearing and speed.
        public override float XGearRatio { get => 1; set { } }
        public override float YGearRatio { get => 1; set { } }
        public override int XSpeed { get => 0; set { } }
        public override int YSpeed { get => 0; set { } }

        public override bool ReverseAzimuth {
            get => Properties.Settings.Default.AscomReverseAzimuth;
            set {
                Properties.Settings.Default.AscomReverseAzimuth = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public override bool ReverseAltitude {
            get => Properties.Settings.Default.AscomReverseAltitude;
            set {
                Properties.Settings.Default.AscomReverseAltitude = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        /// <summary>Azimuth backlash in arcminutes, used only when the device does not handle its own.</summary>
        public override float XBacklashCompensation {
            get => Properties.Settings.Default.AscomXBacklashCompensation;
            set {
                Properties.Settings.Default.AscomXBacklashCompensation = Math.Max(0, value);
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        /// <summary>Altitude backlash in arcminutes, used only when the device does not handle its own.</summary>
        public float YBacklashCompensation {
            get => Properties.Settings.Default.AscomYBacklashCompensation;
            set {
                Properties.Settings.Default.AscomYBacklashCompensation = Math.Max(0, value);
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        protected override float GetBacklashCompensation(Axis axis) {
            if (AscomSystem?.BacklashHandled(axis) == true) {
                return 0f;
            }
            return axis switch {
                Axis.XAxis => XBacklashCompensation,
                Axis.YAxis => YBacklashCompensation,
                _ => 0f,
            };
        }
    }
}
