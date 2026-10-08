using System.ComponentModel.Composition;
using System.Windows;

namespace PolarAlignLive.UI {

    /// <summary>MEF-exported so NINA merges these templates into Application.Resources.</summary>
    [Export(typeof(ResourceDictionary))]
    public partial class PolarAlignLiveTemplates : ResourceDictionary {
        public PolarAlignLiveTemplates() {
            InitializeComponent();
        }
    }
}
