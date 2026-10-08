using NINA.Plugin;
using NINA.Profile.Interfaces;
using System.ComponentModel.Composition;

namespace PolarAlignLive {

    [Export(typeof(NINA.Plugin.Interfaces.IPluginManifest))]
    public class PolarAlignLivePlugin : PluginBase {

        [ImportingConstructor]
        public PolarAlignLivePlugin(IProfileService profileService) {
            // Nothing to initialise; the dockable VM is exported separately.
        }
    }
}
