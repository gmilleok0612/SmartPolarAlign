using NINA.Plugin;
using NINA.Profile.Interfaces;
using System.ComponentModel.Composition;

namespace PolarAlignLive {

    [Export(typeof(NINA.Plugin.Interfaces.IPluginManifest))]
    public class PolarAlignLivePlugin : PluginBase {

        [ImportingConstructor]
        public PolarAlignLivePlugin(IProfileService profileService) {
        }

        /// <summary>Bound by the plugin Options page template ("Polar Align Live_Options").</summary>
        public NightTheme Theme => NightTheme.Instance;
    }
}
