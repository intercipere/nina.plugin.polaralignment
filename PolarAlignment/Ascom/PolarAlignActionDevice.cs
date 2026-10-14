using NINA.Equipment.Interfaces.Mediator;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NINA.Plugins.PolarAlignment.Ascom {

    /// <summary>
    /// An ASCOM or Alpaca device that implements the PolarAlign Actions. Today that is the mount connected in
    /// N.I.N.A.; a separately opened device (e.g. a Switch for a platform under the mount) can implement the
    /// same interface later.
    /// </summary>
    public interface IPolarAlignActionDevice {
        string Name { get; }
        bool Connected { get; }
        IReadOnlyCollection<string> SupportedActions { get; }
        string Action(string actionName, string actionParameters);
    }

    /// <summary>The mount connected in N.I.N.A.: the actions travel over its existing connection.</summary>
    public sealed class ConnectedMountActionDevice : IPolarAlignActionDevice {
        private readonly ITelescopeMediator telescopeMediator;

        public ConnectedMountActionDevice(ITelescopeMediator telescopeMediator) {
            this.telescopeMediator = telescopeMediator ?? throw new ArgumentNullException(nameof(telescopeMediator));
        }

        public string Name {
            get {
                var name = telescopeMediator.GetInfo()?.Name;
                return string.IsNullOrWhiteSpace(name) ? "The mount" : name;
            }
        }

        public bool Connected => telescopeMediator.GetInfo()?.Connected == true;

        // N.I.N.A. reads SupportedActions once, when the mount connects.
        public IReadOnlyCollection<string> SupportedActions =>
            telescopeMediator.GetInfo()?.SupportedActions?.ToList() ?? (IReadOnlyCollection<string>)Array.Empty<string>();

        public string Action(string actionName, string actionParameters) {
            // The mediator answers null when no mount is connected; driver errors arrive as exceptions.
            var reply = telescopeMediator.Action(actionName, actionParameters);
            if (reply == null && !Connected) {
                throw new InvalidOperationException($"{Name} is not connected");
            }
            return reply ?? string.Empty;
        }
    }
}
