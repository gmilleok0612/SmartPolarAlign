using CommunityToolkit.Mvvm.Input;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Equipment.Model;
using NINA.Image.Interfaces;
using NINA.PlateSolving;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel;
using PolarAlignLive.Astro;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace PolarAlignLive {

    /// <summary>
    /// Dockable tool: Imaging tab > Polar Align Live.
    /// Workflow: capture three plate-solved frames while rotating RA (Dec untouched) to find the mount RA axis,
    /// then solve continuously and show how far / which way the axis must move to reach the celestial pole.
    /// All awaits keep the UI synchronization context (no ConfigureAwait(false)) so property changes stay on the UI thread.
    /// </summary>
    [Export(typeof(IDockableVM))]
    public class PolarAlignLiveVM : DockableVM {

        private readonly IProfileService profileService;
        private readonly ICameraMediator cameraMediator;
        private readonly ITelescopeMediator telescopeMediator;
        private readonly IImagingMediator imagingMediator;
        private readonly IFilterWheelMediator filterWheelMediator;
        private readonly IPlateSolverFactory plateSolverFactory;

        private readonly List<SolvedFrame> frames = new List<SolvedFrame>();
        private readonly List<double> mountDecs = new List<double>();
        private AxisSolution axis;
        private CancellationTokenSource cts;

        public override bool IsTool => true;

        [ImportingConstructor]
        public PolarAlignLiveVM(IProfileService profileService,
                                ICameraMediator cameraMediator,
                                ITelescopeMediator telescopeMediator,
                                IImagingMediator imagingMediator,
                                IFilterWheelMediator filterWheelMediator,
                                IPlateSolverFactory plateSolverFactory) : base(profileService) {
            this.profileService = profileService;
            this.cameraMediator = cameraMediator;
            this.telescopeMediator = telescopeMediator;
            this.imagingMediator = imagingMediator;
            this.filterWheelMediator = filterWheelMediator;
            this.plateSolverFactory = plateSolverFactory;

            LoadAutoSettings();
            Title = "Polar Align Live";
            CanClose = false;

            exposureTime = 2.0;
            gain = -1;
            binning = Math.Max((short)1, profileService.ActiveProfile.PlateSolveSettings.Binning);
            status = "Idle";
            instructions = "Point at a plate-solvable field, then press Find Axis (the mount moves itself).";

            StartLiveCommand = new AsyncRelayCommand(StartLive, () => !IsBusy && axis != null);
            StopCommand = new RelayCommand(Stop, () => liveRunning);
            ResetCommand = new RelayCommand(Reset, () => !IsBusy);
            AutoCaptureCommand = new AsyncRelayCommand(AutoCapture, () => !IsBusy && axis == null);
            AbortSlewCommand = new RelayCommand(AbortSlew, () => autoRunning);
            ToggleFlashCommand = new RelayCommand(() => { flashOn = !flashOn; SaveAutoSettings(); RaisePropertyChanged(nameof(FlashText)); if (flashOn) Flash(); });
            ToggleRefractionCommand = new RelayCommand(() => { refrOn = !refrOn; SaveAutoSettings(); RaisePropertyChanged(nameof(RefractionText)); });
            ToggleBeepCommand = new RelayCommand(() => { beepOn = !beepOn; SaveAutoSettings(); RaisePropertyChanged(nameof(BeepText)); if (beepOn) Beep(); });
            CycleAltUnitCommand = new RelayCommand(() => { altUnit = (altUnit + 1) % 3; altCal = 0; AfterCalChange("Alt knob unit changed; run Calibrate Adjustment Knobs again."); });
            CycleAzUnitCommand = new RelayCommand(() => { azUnit = (azUnit + 1) % 3; azCal = 0; AfterCalChange("Az knob unit changed; run Calibrate Adjustment Knobs again."); });
            CalibrateKnobsCommand = new AsyncRelayCommand(CalibrateKnobsGuarded);
            CalContinueCommand = new RelayCommand(() => calTcs?.TrySetResult(true));
            CalCancelCommand = new RelayCommand(() => {
                if (!calRunning) { CalVisible = false; return; }
                calTcs?.TrySetResult(false); try { cts?.Cancel(); } catch { }
            });
            ToggleSettingsCommand = new RelayCommand(() => SettingsOpen = !SettingsOpen);
            // One-click open from the top-bar icon: NINA's icon toggles IsVisible, so keep it in step with what the
            // dock really shows, and bring the tab to the front whenever the panel becomes visible.
            PropertyChanged += (s, e) => {
                if (e.PropertyName == nameof(IsVisible) && IsVisible)
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => maximizer.Activate(this)),
                        System.Windows.Threading.DispatcherPriority.Background);
            };
            TrySetIcon();
            StartVisibilitySync();
            MaximizeCommand = new RelayCommand(() => {
                Status = maximizer.Toggle(this);
                RaisePropertyChanged(nameof(MaximizeText));
            });
            ConfirmSlewCommand = new RelayCommand(() => confirmTcs?.TrySetResult(true));
            CancelSlewCommand = new RelayCommand(() => confirmTcs?.TrySetResult(false));
            DimmerUpCommand = new RelayCommand(() => NightTheme.Instance.StepDimmer(0.1));
            DimmerDownCommand = new RelayCommand(() => NightTheme.Instance.StepDimmer(-0.1));
            ToggleRemoteCommand = new RelayCommand(() => { RemoteEnabled = !RemoteEnabled; });
            NewRemoteCodeCommand = new RelayCommand(() => { remoteAuth.NewCode(); ApplyRemote(); });
            ApplyRemote();
        }

        // ---------- phone access (small web server in the plugin; the phone opens the page in its browser) ----------

        private PolarAlignLive.Remote.RemoteServer remoteServer;
        private bool remoteEnabled;
        private int remotePort = 5050;
        private readonly PolarAlignLive.Remote.RemoteAuth remoteAuth = new PolarAlignLive.Remote.RemoteAuth();
        private bool remoteWasOn;
        private string remoteMessage = "";
        public ICommand ToggleRemoteCommand { get; }
        public ICommand NewRemoteCodeCommand { get; }

        public bool RemoteEnabled { get => remoteEnabled; set { remoteEnabled = value; SaveAutoSettings(); ApplyRemote(); } }
        public string RemoteToggleText => remoteEnabled ? "Phone access: On" : "Phone access: Off";
        public string RemotePortText {
            get => remotePort.ToString(CultureInfo.InvariantCulture);
            set { if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 1024 && v <= 65535) { remotePort = v; SaveAutoSettings(); ApplyRemote(); } RaisePropertyChanged(); }
        }
        public string RemoteInfoText => remoteMessage;

        private int solveSeq;
        /// <summary>Counts new Live View solutions so the phone can beep/vibrate on each one.</summary>
        public int SolveSeq => solveSeq;

        private static string LocalAddresses() {
            var list = new List<string>();
            try {
                foreach (var a in System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName()).AddressList) {
                    if (a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    string t = a.ToString();
                    if (t.StartsWith("169.254.") || t.StartsWith("127.")) continue;
                    list.Add(t);
                }
            } catch { }
            return list.Count == 0 ? "<this PC's IP>" : string.Join(" or ", list);
        }

        private void ApplyRemote() {
            try {
                if (remoteServer == null) {
                    var bridge = new PolarAlignLive.Remote.RemoteBridge(this);
                    remoteServer = new PolarAlignLive.Remote.RemoteServer(bridge.BuildState, bridge.RunCommand, remoteAuth);
                }
                if (remoteEnabled) {
                    if (remoteServer.Start(remotePort)) {
                        // A fresh random pairing code every time phone access is switched on (or after it has expired).
                        if (!remoteWasOn || !remoteAuth.HasValidCode) remoteAuth.NewCode();
                        remoteWasOn = true;
                        string ips = LocalAddresses();
                        remoteMessage = "Open on your phone (same Wi-Fi): http://" + ips.Replace(" or ", ":" + remotePort + " or http://") + ":" + remotePort +
                                        "\nPairing code: " + remoteAuth.DisplayCode + " (6 digits; type it on the phone once; valid 15 minutes; press New pairing code for another).";
                    } else remoteMessage = "Phone access could not start: " + remoteServer.LastError + " Try another port.";
                } else {
                    remoteServer.Stop();
                    remoteAuth.Clear();
                    remoteWasOn = false;
                    remoteMessage = "";
                }
            } catch (Exception ex) { remoteMessage = "Phone access error: " + ex.Message; }
            RaisePropertyChanged(nameof(RemoteToggleText));
            RaisePropertyChanged(nameof(RemoteInfoText));
            RaisePropertyChanged(nameof(RemoteEnabled));
        }

        public ICommand StartLiveCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand ResetCommand { get; }
        public ICommand AutoCaptureCommand { get; }
        public ICommand AbortSlewCommand { get; }
        public ICommand CycleAltUnitCommand { get; }
        public ICommand CycleAzUnitCommand { get; }
        public ICommand CalibrateKnobsCommand { get; }
        public ICommand ToggleBeepCommand { get; }

        private bool flashOn = true;
        public bool FlashOn => flashOn;
        public bool BeepOn => beepOn;
        public string FlashText => flashOn ? "Live flash: On" : "Live flash: Off";
        public ICommand ToggleFlashCommand { get; private set; }

        private bool adviceFlash;
        /// <summary>True for a moment after each new Live solution: the advice text is drawn brighter.</summary>
        public bool AdviceFlash { get => adviceFlash; private set { if (adviceFlash == value) return; adviceFlash = value; RaisePropertyChanged(); } }
        private int flashGen;
        private async void Flash() {
            if (!flashOn) return;
            int gen = ++flashGen;
            try {
                AdviceFlash = true;
                await Task.Delay(600);
                if (gen == flashGen) AdviceFlash = false;
            } catch { AdviceFlash = false; }
        }

        /// <summary>True when the profile latitude is south of the equator (the mount then aligns on the south celestial pole).</summary>
        public bool SouthHemisphere { get { try { return profileService.ActiveProfile.AstrometrySettings.Latitude < 0; } catch { return false; } } }

        private bool refrOn = true;
        public string RefractionText => refrOn ? "Refraction: On" : "Refraction: Off";
        public ICommand ToggleRefractionCommand { get; private set; }

        /// <summary>Refraction lift of the pole (arcmin) for this site, or 0 when the option is off.</summary>
        private double RefractionNow(double latDeg) {
            if (!refrOn) return 0;
            double elev = 0;
            try {
                var p = profileService.ActiveProfile.AstrometrySettings.GetType().GetProperty("Elevation");
                if (p != null) elev = Convert.ToDouble(p.GetValue(profileService.ActiveProfile.AstrometrySettings));
            } catch { }
            if (double.IsNaN(elev) || elev < -400 || elev > 6000) elev = 0;
            return PolarMath.RefractionArcmin(Math.Abs(latDeg), elev);   // altitude of the pole = |latitude| in either hemisphere
        }

        private bool beepOn = true;
        public string BeepText => beepOn ? "Live sound: On" : "Live sound: Off";
        /// <summary>Short beep so the user knows Live View just produced new advice.</summary>
        // What the user should do right now during Live: 0 = nothing shown, 1 = adjust now, 2 = hands off (picture being taken), 3 = solving.
        private int livePhase;
        private string livePhaseText = "";
        public bool PhaseVisible => livePhase != 0;
        public bool PhaseAdjust => livePhase == 1;
        public string PhaseText => livePhaseText;
        private void SetPhase(int phase, string text) {
            if (livePhase == phase && livePhaseText == text) return;
            livePhase = phase; livePhaseText = text;
            RaisePropertyChanged(nameof(PhaseVisible)); RaisePropertyChanged(nameof(PhaseAdjust)); RaisePropertyChanged(nameof(PhaseText));
        }

        // Soft bell-like cues (generated once, played through the Windows audio device, not the PC speaker).
        private static readonly Dictionary<string, byte[]> soundCache = new Dictionary<string, byte[]>();
        private static byte[] SoftWav(string key, double[] freqs, double noteSec) {
            lock (soundCache) {
                if (soundCache.TryGetValue(key, out var have)) return have;
                const int sr = 22050;
                int per = (int)(sr * noteSec), n = per * freqs.Length;
                var ms = new System.IO.MemoryStream();
                var w = new System.IO.BinaryWriter(ms);
                w.Write(new[] { 'R', 'I', 'F', 'F' }); w.Write(36 + n * 2); w.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
                w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(sr); w.Write(sr * 2); w.Write((short)2); w.Write((short)16);
                w.Write(new[] { 'd', 'a', 't', 'a' }); w.Write(n * 2);
                for (int f = 0; f < freqs.Length; f++) {
                    for (int i = 0; i < per; i++) {
                        double t = i / (double)sr;
                        double att = Math.Min(1.0, i / (sr * 0.03)), rel = Math.Min(1.0, (per - i) / (sr * 0.03));
                        double env = att * rel * Math.Exp(-3.2 * i / per);
                        double smp = Math.Sin(2 * Math.PI * freqs[f] * t) + 0.25 * Math.Sin(4 * Math.PI * freqs[f] * t);
                        w.Write((short)(smp * env * 0.26 * 32767));
                    }
                }
                w.Flush();
                var bytes = ms.ToArray();
                soundCache[key] = bytes;
                return bytes;
            }
        }
        private void PlaySoft(string key, double[] freqs, double noteSec) {
            if (!beepOn) return;
            var bytes = SoftWav(key, freqs, noteSec);
            System.Threading.Tasks.Task.Run(() => {
                try { using (var sp = new System.Media.SoundPlayer(new System.IO.MemoryStream(bytes))) sp.PlaySync(); }
                catch { try { System.Media.SystemSounds.Beep.Play(); } catch { } }
            });
        }
        /// <summary>Soft rising pair: a new reading has arrived / adjust now.</summary>
        private void Beep() => PlaySoft("up", new[] { 523.25, 659.25 }, 0.3);
        /// <summary>Soft falling pair: the exposure is starting, hands off the mount.</summary>
        private void BeepHandsOff() => PlaySoft("down", new[] { 659.25, 440.0 }, 0.26);
        /// <summary>Single soft low note: two seconds left to finish the adjustment.</summary>
        private void BeepWarn() => PlaySoft("warn", new[] { 440.0 }, 0.3);
        public ICommand CalContinueCommand { get; }
        public ICommand CalCancelCommand { get; }
        public ICommand ToggleSettingsCommand { get; }
        public ICommand MaximizeCommand { get; }

        /// <summary>NINA's top-bar icon calls this to toggle IsVisible. The flag can disagree with what the dock really
        /// shows (it starts true even when the panel is hidden), which made the first click do nothing visible.
        /// So at click time, first make the flag match the dock, then toggle.</summary>
        public override void Hide(object o) {
            try {
                var found = maximizer.State(this, out var vis, out var sel);
                NINA.Core.Utility.Logger.Info($"PolarAlignLive icon click: flag={IsVisible} found={found} dockVisible={vis} selected={sel}");
                if (!found) {
                    // Dock not reachable (lazy tab): force a hide->show cycle so the click always shows the panel.
                    IsVisible = false; IsVisible = true;
                    return;
                }
                if (vis && !sel) {
                    // Open but buried behind another tab: bring it to the front instead of hiding it.
                    IsVisible = true;
                    maximizer.Activate(this);
                    return;
                }
                if (IsVisible != vis) IsVisible = vis;
            } catch (Exception ex) { NINA.Core.Utility.Logger.Info("PolarAlignLive Hide: " + ex.Message); }
            base.Hide(o);
            NINA.Core.Utility.Logger.Info($"PolarAlignLive icon click done: flag={IsVisible}");
        }

        private bool iconSet;

        /// <summary>Replaces NINA's default puzzle-piece icon with ours once our resource dictionary is merged.</summary>
        private void TrySetIcon() {
            if (iconSet) return;
            try {
                if (System.Windows.Application.Current?.Resources["PolarAlignLiveSVG"] is System.Windows.Media.GeometryGroup g) {
                    ImageGeometry = g;
                    iconSet = true;
                }
            } catch { }
        }

        /// <summary>After NINA loads its dock layout, make IsVisible match whether the panel is really shown.
        /// Without this a panel that is hidden but flagged visible needs two clicks on the top-bar icon.</summary>
        private void StartVisibilitySync() {
            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp == null) return;
            int tries = 0;
            bool visibilitySynced = false;
            bool recovered = false;
            var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, disp) {
                Interval = TimeSpan.FromSeconds(2)
            };
            timer.Tick += (s, e) => {
                tries++;
                TrySetIcon();
                var shown = maximizer.PanelVisible(this);
                if (shown.HasValue && !visibilitySynced) {
                    if (IsVisible != shown.Value) IsVisible = shown.Value;
                    visibilitySynced = true;
                }
                if (!recovered && maximizer.RecoverHidden(this) >= 0) recovered = true;
                if ((visibilitySynced && iconSet && recovered) || tries >= 30) timer.Stop();
            };
            timer.Start();
        }
        private readonly PolarAlignLive.UI.PanelMaximizer maximizer = new PolarAlignLive.UI.PanelMaximizer();
        public string MaximizeText => maximizer.IsMaximized ? "Restore" : "Maximize";
        public ICommand ConfirmSlewCommand { get; }
        public ICommand CancelSlewCommand { get; }
        public ICommand DimmerUpCommand { get; }
        public ICommand DimmerDownCommand { get; }

        public NightTheme Theme => NightTheme.Instance;

        // ---------- bindable state ----------

        private double exposureTime;
        public double ExposureTime { get => exposureTime; set { exposureTime = Math.Max(0.1, value); RaisePropertyChanged(); } }

        private int gain;
        public int Gain { get => gain; set { gain = value; RaisePropertyChanged(); } }

        private short binning;
        public short Binning { get => binning; set { binning = Math.Max((short)1, value); RaisePropertyChanged(); } }

        private double autoStepDeg = 20;
        /// <summary>RA step between auto-captured frames, degrees (15..40).</summary>
        public double AutoStepDeg { get => autoStepDeg; set { autoStepDeg = Math.Max(15, Math.Min(40, value)); RaisePropertyChanged(); SaveAutoSettings(); } }

        private double minAltDeg = 20;
        public double MinAltDeg { get => minAltDeg; set { minAltDeg = Math.Max(5, Math.Min(80, value)); RaisePropertyChanged(); SaveAutoSettings(); } }

        private int pauseSec = 5;
        /// <summary>Seconds to wait after each Live update before the next exposure, so the user has time to adjust.</summary>
        public int PauseSeconds { get => pauseSec; set { pauseSec = Math.Max(0, Math.Min(60, value)); RaisePropertyChanged(); SaveAutoSettings(); } }

        private double maxAltDeg = 60;
        /// <summary>Zenith guard: auto slews never go above this altitude (protects long tubes from the pier).</summary>
        public double MaxAltDeg { get => maxAltDeg; set { maxAltDeg = Math.Max(20, Math.Min(85, value)); RaisePropertyChanged(); SaveAutoSettings(); } }

        private static string AutoSettingsPath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NINA", "PolarAlignLive.auto.settings");

        private void LoadAutoSettings() {
            try {
                if (!System.IO.File.Exists(AutoSettingsPath)) return;
                foreach (var line in System.IO.File.ReadAllLines(AutoSettingsPath)) {
                    var kv = line.Split('=');
                    if (kv.Length == 2 && kv[0] == "calmount") { calMount = kv[1]; continue; }
                    if (kv.Length != 2 || !double.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) continue;
                    if (kv[0] == "step") autoStepDeg = Math.Max(15, Math.Min(40, v));
                    else if (kv[0] == "minalt") minAltDeg = Math.Max(5, Math.Min(80, v));
                    else if (kv[0] == "maxalt") maxAltDeg = Math.Max(20, Math.Min(85, v));
                    else if (kv[0] == "pause") pauseSec = (int)Math.Max(0, Math.Min(60, v));
                    else if (kv[0] == "crop") cropPercent = v == 50 ? 50 : v == 25 ? 25 : 100;
                    else if (kv[0] == "settings") settingsOpen = v == 1;
                    else if (kv[0] == "altunit") altUnit = (int)Math.Max(0, Math.Min(2, v));
                    else if (kv[0] == "azunit") azUnit = (int)Math.Max(0, Math.Min(2, v));
                    else if (kv[0] == "beep") beepOn = v != 0;
                    else if (kv[0] == "refr") refrOn = v != 0;
                    else if (kv[0] == "flash") flashOn = v != 0;
                    else if (kv[0] == "remote") remoteEnabled = v != 0;
                    else if (kv[0] == "rport") remotePort = (int)Math.Max(1024, Math.Min(65535, v));
                    else if (kv[0] == "altcal2") altCal = v;
                    else if (kv[0] == "azcal2") azCal = v;
                }
            } catch { }
        }

        private void SaveAutoSettings() {
            try {
                System.IO.File.WriteAllLines(AutoSettingsPath, new[] {
                    "step=" + autoStepDeg.ToString(CultureInfo.InvariantCulture),
                    "minalt=" + minAltDeg.ToString(CultureInfo.InvariantCulture),
                    "maxalt=" + maxAltDeg.ToString(CultureInfo.InvariantCulture),
                    "pause=" + pauseSec.ToString(CultureInfo.InvariantCulture),
                    "crop=" + cropPercent.ToString(CultureInfo.InvariantCulture),
                    "settings=" + (settingsOpen ? "1" : "0"),
                    "beep=" + (beepOn ? "1" : "0"),
                    "refr=" + (refrOn ? "1" : "0"),
                    "flash=" + (flashOn ? "1" : "0"),
                    "remote=" + (remoteEnabled ? "1" : "0"),
                    "rport=" + remotePort.ToString(CultureInfo.InvariantCulture),
                    "altunit=" + altUnit.ToString(CultureInfo.InvariantCulture),
                    "azunit=" + azUnit.ToString(CultureInfo.InvariantCulture),
                    "altcal2=" + altCal.ToString("R", CultureInfo.InvariantCulture),
                    "azcal2=" + azCal.ToString("R", CultureInfo.InvariantCulture),
                    "calmount=" + calMount });
            } catch { }
        }

        private int cropPercent = 100;
        /// <summary>Centered hardware sub-frame: 100 (full), 50 or 25 percent of the sensor width/height.</summary>
        public int CropPercent { get => cropPercent; set { cropPercent = value == 50 ? 50 : value == 25 ? 25 : 100; RaisePropertyChanged(); RaisePropertyChanged(nameof(Crop100)); RaisePropertyChanged(nameof(Crop50)); RaisePropertyChanged(nameof(Crop25)); SaveAutoSettings(); } }
        public bool Crop100 { get => cropPercent == 100; set { if (value) CropPercent = 100; } }
        public bool Crop50 { get => cropPercent == 50; set { if (value) CropPercent = 50; } }
        public bool Crop25 { get => cropPercent == 25; set { if (value) CropPercent = 25; } }

        private bool settingsOpen;
        /// <summary>Shows/hides the Exp/Gain/Bin, Auto, Crop and Night rows so the bullseye can use the space.</summary>
        public bool SettingsOpen { get => settingsOpen; set { settingsOpen = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(SettingsToggleText)); SaveAutoSettings(); } }
        public string SettingsToggleText => settingsOpen ? "Settings \u25B4" : "Settings \u25BE";

        private bool confirmPending;
        public bool ConfirmPending { get => confirmPending; private set { confirmPending = value; RaisePropertyChanged(); } }

        private string confirmText = "";
        private string confirmYesText = "SLEW";
        public string ConfirmYesText { get => confirmYesText; private set { confirmYesText = value; RaisePropertyChanged(); } }
        public string ConfirmText { get => confirmText; private set { confirmText = value; RaisePropertyChanged(); } }

        private TaskCompletionSource<bool> confirmTcs;
        private bool autoRunning;
        private volatile bool allStop;
        private volatile MoveGate moveGate;
        private const string AllStopDoneText = "All Stop. The mount was stopped; the last exposure and solve were allowed to finish. Press Find Axis to start again.";

        private bool isBusy;
        public bool IsBusy { get => isBusy; private set { isBusy = value; RaisePropertyChanged(); NotifyCommands(); } }

        private string status;
        public string Status { get => status; private set { status = value; RaisePropertyChanged(); } }

        private string instructions;
        public string Instructions { get => instructions; private set { instructions = value; RaisePropertyChanged(); } }

        private string frameCountText = "Frames: 0 / 3";
        public string FrameCountText { get => frameCountText; private set { frameCountText = value; RaisePropertyChanged(); } }

        private string axisQualityText = "";
        public string AxisQualityText { get => axisQualityText; private set { axisQualityText = value; RaisePropertyChanged(); } }

        private bool hasSolution;
        public bool HasSolution { get => hasSolution; private set { hasSolution = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(AlignmentQualityText)); RaisePropertyChanged(nameof(AlignmentLevel)); } }

        private double moveUpArcmin;
        public double MoveUpArcmin { get => moveUpArcmin; private set { moveUpArcmin = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(MoveUpText)); RaisePropertyChanged(nameof(AltAdviceText)); } }

        private double moveEastArcmin;
        public double MoveEastArcmin { get => moveEastArcmin; private set { moveEastArcmin = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(MoveEastText)); RaisePropertyChanged(nameof(AzAdviceText)); } }

        private double totalErrorArcmin;
        public double TotalErrorArcmin { get => totalErrorArcmin; private set { totalErrorArcmin = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(TotalErrorText)); RaisePropertyChanged(nameof(AlignmentQualityText)); RaisePropertyChanged(nameof(AlignmentLevel)); } }

        public string MoveUpText => !HasSolution ? "ALT  --" :
            (Math.Abs(MoveUpArcmin) < 0.5 ? "ALT  OK" : $"ALT  {(MoveUpArcmin > 0 ? "RAISE axis" : "LOWER axis")}  {Fmt(Math.Abs(MoveUpArcmin))}");

        public string MoveEastText => !HasSolution ? "AZ  --" :
            (Math.Abs(MoveEastArcmin) < 0.5 ? "AZ  OK" : $"AZ  swing axis {(MoveEastArcmin > 0 ? "EAST" : "WEST")}  {Fmt(Math.Abs(MoveEastArcmin))}");

        /// <summary>Fixed verdict (not user-adjustable): under 1' excellent, 1-3' good enough for guided imaging.</summary>
        public int AlignmentLevel => !HasSolution || axis == null ? 0 : TotalErrorArcmin < 1.0 ? 2 : TotalErrorArcmin <= 3.0 ? 1 : 0;
        public string AlignmentQualityText => AlignmentLevel == 2 ? "ALIGNMENT EXCELLENT" : AlignmentLevel == 1 ? "ALIGNMENT GOOD ENOUGH FOR GUIDING" : "";

        public string TotalErrorText => !HasSolution ? "Polar error  --" : $"Polar error  {Fmt(TotalErrorArcmin)}";

        private static string Fmt(double arcmin) =>
            arcmin >= 60 ? (arcmin / 60.0).ToString("0.00", CultureInfo.InvariantCulture) + "°"
                         : arcmin.ToString("0.0", CultureInfo.InvariantCulture) + "′";

        private void NotifyCommands() {
            // Always called from UI-context continuations or UI commands.
            (AutoCaptureCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
            (StartLiveCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
            (StopCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (AbortSlewCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (ResetCommand as RelayCommand)?.NotifyCanExecuteChanged();
        }

        // ---------- adjuster-knob calibration (guided) ----------
        // Units per axis: 0 = tics, 1 = turns, 2 = degrees of knob rotation. Calibration = arcminutes the axis moves per
        // unit turned clockwise (signed). 0 = not calibrated. Stored in the settings file.

        private int altUnit, azUnit;
        private double altCal, azCal;
        /// <summary>Name of the mount the calibration was measured on. A calibration from another mount (or a simulator) is discarded.</summary>
        private string calMount = "";
        private string CurrentMountName() {
            try { return (telescopeMediator.GetInfo().Name ?? "").Replace("=", "-").Replace("\r", " ").Replace("\n", " ").Trim(); } catch { return ""; }
        }
        /// <summary>Calibration is a property of one physical mount. If the connected mount is not the one it was measured on, forget it.</summary>
        private void CheckCalMount() {
            if (altCal == 0 && azCal == 0) return;
            string now = CurrentMountName();
            if (now.Length == 0 || string.Equals(now, calMount, StringComparison.Ordinal)) return;
            NINA.Core.Utility.Logger.Info($"PolarAlignLive: discarding knob calibration (measured on '{calMount}', connected mount is '{now}')");
            altCal = 0; azCal = 0; calMount = "";
            SaveAutoSettings(); RaiseCalText();
        }

        private static string UnitLabel(int u) => u == 1 ? "turns" : u == 2 ? "degrees" : "tics";
        /// <summary>Test turn per attempt: 6 tics, half a turn, or 180 degrees.</summary>
        private static double CalStep(int u) => u == 1 ? 0.5 : u == 2 ? 180 : 6;
        private static string CalStepText(int u) => u == 1 ? "1/2 turn" : u == 2 ? "180 degrees" : "6 tics";

        public string AltUnitText => "Alt knob: " + UnitLabel(altUnit);
        public string AzUnitText => "Az knob: " + UnitLabel(azUnit);

        public string AltAdviceText => Advice(MoveUpArcmin, altCal, altUnit, " (altitude knob)", n => n > 0 ? "axis UP" : "axis DOWN");
        public string AzAdviceText => Advice(MoveEastArcmin, azCal, azUnit, " (LEFT azimuth knob)", n => n > 0 ? "axis EAST" : "axis WEST");

        private string Advice(double needArcmin, double cal, int unit, string who, Func<double, string> effect) {
            if (!HasSolution || Math.Abs(needArcmin) < 0.5) return "";
            if (cal == 0) return "not calibrated";
            double u = needArcmin / cal;
            double m = Math.Abs(u);
            string amount = unit == 1 ? m.ToString("0.00", CultureInfo.InvariantCulture) + " turns" + (m < 0.25 ? $" (about {(m * 360).ToString("0", CultureInfo.InvariantCulture)}\u00B0 of knob rotation)" : "")
                          : unit == 2 ? m.ToString("0", CultureInfo.InvariantCulture) + " degrees"
                          : m.ToString("0.0", CultureInfo.InvariantCulture) + " tics";
            return $"\u2248 {amount} {(u > 0 ? "clockwise" : "counter-clockwise")}{who}  \u2192 {effect(needArcmin)}";
        }

        /// <summary>Shown when Live starts: without calibration there is no knob guidance (turns/tics/degrees), only the distances.</summary>
        private string calNoteText = "";
        public string CalNoteText { get => calNoteText; private set { calNoteText = value; RaisePropertyChanged(); } }
        private string CalNote() {
            if (altCal != 0 && azCal != 0) return "";
            string which = altCal == 0 && azCal == 0 ? "Knobs not calibrated" : altCal == 0 ? "Altitude knob not calibrated" : "Azimuth knob not calibrated";
            return which + ": distance only, no turn guidance. Stop Live, then Calibrate Adjustment Knobs.";
        }

        private void RaiseCalText() {
            RaisePropertyChanged(nameof(AltUnitText)); RaisePropertyChanged(nameof(AzUnitText));
            RaisePropertyChanged(nameof(AltAdviceText)); RaisePropertyChanged(nameof(AzAdviceText));
        }

        /// <summary>Calibration is all-or-nothing: if either knob fails, nothing is saved and the user is told to run it again.</summary>
        private string CalFail(string reason) =>
            "CALIBRATION FAILED \u2013 " + reason + "\n\nNothing was saved. Press Calibrate Adjustment Knobs to try again, and wait for the closing \"Calibrated\" message (both knobs)."
            + ((altCal == 0 || azCal == 0) ? "" : "\n\nYour previous calibration is still in use.");

        private void AfterCalChange(string msg) { SaveAutoSettings(); RaiseCalText(); Status = msg; }

        // wizard overlay state
        private bool calVisible;
        public bool CalVisible { get => calVisible; private set { calVisible = value; RaisePropertyChanged(); } }
        private string calText = "";
        public string CalText { get => calText; private set { calText = value; RaisePropertyChanged(); } }
        private bool calCanContinue;
        public bool CalCanContinue { get => calCanContinue; private set { calCanContinue = value; RaisePropertyChanged(); } }
        private TaskCompletionSource<bool> calTcs;
        private bool calRunning;
        public string CalCancelText => calRunning ? "Cancel" : "Close";
        private void SetCalRunning(bool v) { calRunning = v; RaisePropertyChanged(nameof(CalCancelText)); }

        /// <summary>Shows a short message in the overlay (only Cancel is offered; it closes the overlay).</summary>
        private void ShowCalMessage(string text) { CalText = text; CalCanContinue = false; CalVisible = true; }

        private async Task<bool> WaitForCalContinue(CancellationToken ct) {
            calTcs = new TaskCompletionSource<bool>();
            CalCanContinue = true;
            using (ct.Register(() => calTcs.TrySetResult(false))) {
                bool r = await calTcs.Task;
                CalCanContinue = false;
                return r;
            }
        }

        /// <summary>Capture + solve (up to 3 tries) and return how far the RA axis is from the pole right now.</summary>
        private async Task<(bool ok, double up, double east)> MeasureOnce(CancellationToken ct) {
            for (int attempt = 0; attempt < 3; attempt++) {
                var solver = BuildSolver(true, out var parameter, out var seq);
                var result = await SolveOnce(solver, parameter, seq, ct);
                if (result == null || !result.Success || result.Coordinates == null) continue;
                var live = ToFrame(result);
                var a = PolarMath.AxisOnSky(axis, live);
                a.ToRaDec(out double aRa, out double aDec);
                var ofDate = new Coordinates(Angle.ByDegree(aRa), Angle.ByDegree(aDec), Epoch.J2000).Transform(Epoch.JNOW);
                var a2 = profileService.ActiveProfile.AstrometrySettings;
                PolarMath.PoleError(ofDate.RADegrees, ofDate.Dec, a2.Latitude, DateTime.UtcNow, a2.Longitude,
                                    out double up, out double east, out double total, RefractionNow(a2.Latitude));
                MoveUpArcmin = up; MoveEastArcmin = east; TotalErrorArcmin = total; HasSolution = true;
                return (true, up, east);
            }
            return (false, 0, 0);
        }

        /// <summary>Guided calibration: for each axis, measure, ask for a known turn, measure again.</summary>
        private bool calibrating;
        /// <summary>True while the calibration runs: the top instruction/frame text is hidden so it can't be mistaken for a to-do.</summary>
        public bool InstructionsShown => !calibrating;

        private async Task CalibrateKnobsGuarded() {
            if (IsBusy) { ShowCalMessage("Press Stop first (Live or another run is active), then press Calibrate Adjustment Knobs again."); return; }
            calibrating = true; RaisePropertyChanged(nameof(InstructionsShown));
            try { await CalibrateKnobs(); }
            finally { calibrating = false; RaisePropertyChanged(nameof(InstructionsShown)); }
        }

        private async Task CalibrateKnobs() {
            if (!EquipmentReady() || !await EnsureTracking()) { ShowCalMessage(Status); return; }
            if (axis == null) {
                // The knob calibration measures how far the polar AXIS moves, so it needs the axis. Find it first, automatically.
                cts = new CancellationTokenSource();
                ConfirmText = "Calibration is about to begin. It needs to slew the scope. Make sure the cables are clear and the mount is ready to slew.";
                ConfirmYesText = "CONTINUE";
                Status = "Waiting for your confirmation before calibration starts.";
                IsBusy = true;    // blocks Find Axis / Live / Reset while the warning is up; Stop cancels it
                ConfirmPending = true;
                bool go = await WaitForConfirm(cts.Token);
                ConfirmPending = false;
                ConfirmYesText = "SLEW";
                IsBusy = false;
                if (!go) { Status = "Cancelled. The mount was not moved."; return; }
                await AutoCaptureCore(true);
                if (axis == null) {
                    ShowCalMessage("Find Axis did not finish, so the knob calibration was not started.\n\n" + Status + "\n\nPress Calibrate Adjustment Knobs to try again.");
                    return;
                }
            }
            SetCalRunning(true);
            cts = new CancellationTokenSource();
            var ct = cts.Token;
            IsBusy = true;
            CalVisible = true;
            string finalMsg = null;     // shown in the window when the routine ends (null = just close it)
            double newAlt = 0, newAz = 0;   // nothing is saved unless BOTH knobs calibrate
            try {
                for (int axisId = 1; axisId <= 2; axisId++) {
                    string name = axisId == 1 ? "ALTITUDE" : "AZIMUTH";
                    string head = $"{name} knob ({axisId} of 2)\n\n";
                    int unit = axisId == 1 ? altUnit : azUnit;
                    double step = CalStep(unit);

                    CalText = head + "Taking frame to determine exact position in the sky - please wait.";
                    var start = await MeasureOnce(ct);
                    NINA.Core.Utility.Logger.Info($"PolarAlignLive calibrate axis {axisId}: start ok={start.ok} up={start.up:0.00} east={start.east:0.00}");
                    if (!start.ok) { finalMsg = CalFail("the plate solve failed. Check focus and exposure."); return; }

                    double turned = 0, moved = 0;
                    for (int tries = 0; tries < 4; tries++) {
                        turned += step;
                        CalText = head + (tries == 0 ? "" : "Not enough movement measured.\n\n") + "Move the " + (axisId == 1 ? "altitude knob " : "LEFT azimuth knob ") + CalStepText(unit) + " clockwise.";
                        if (!await WaitForCalContinue(ct)) { finalMsg = CalFail("you cancelled it."); return; }

                        CalText = head + "Taking frame to determine exact position in the sky after adjustment - please wait.";
                        var end = await MeasureOnce(CancellationToken.None);   // once you pressed Continue, the measurement always finishes so the turn is not wasted
                        if (!end.ok) { finalMsg = CalFail("the plate solve failed. Check focus and exposure."); return; }
                        moved = axisId == 1 ? start.up - end.up : start.east - end.east;   // how far the axis actually moved (arcmin)
                        NINA.Core.Utility.Logger.Info($"PolarAlignLive calibrate axis {axisId}: try {tries} end ok={end.ok} up={end.up:0.00} east={end.east:0.00} moved={moved:0.00} turned={turned}");
                        if (Math.Abs(moved) >= 1.0) break;
                    }
                    if (Math.Abs(moved) < 1.0) { finalMsg = CalFail("the " + (axisId == 1 ? "altitude" : "azimuth") + " axis did not move. Check that you turned the right knob."); return; }

                    double k = moved / turned;
                    if (axisId == 1) newAlt = k; else newAz = k;
                    NINA.Core.Utility.Logger.Info($"PolarAlignLive calibrate axis {axisId}: {k:0.000} arcmin per {UnitLabel(unit)}");
                }
                if (newAlt == 0 || newAz == 0) { finalMsg = CalFail("both knobs must be measured and one was not."); return; }
                altCal = newAlt; azCal = newAz;
                calMount = CurrentMountName();
                SaveAutoSettings(); RaiseCalText();
                finalMsg = $"Calibrated:\nAltitude {Math.Abs(altCal).ToString("0.0", CultureInfo.InvariantCulture)}\u2032 per {UnitLabel(altUnit).TrimEnd('s')}, azimuth {Math.Abs(azCal).ToString("0.0", CultureInfo.InvariantCulture)}\u2032 per {UnitLabel(azUnit).TrimEnd('s')}.\n\nMeasured: turning the altitude knob clockwise moves the axis {(altCal > 0 ? "UP" : "DOWN")}; turning the LEFT azimuth knob clockwise moves the axis {(azCal > 0 ? "EAST" : "WEST")}. If that is not what you saw, run the calibration again.\n\nStart Live View to view adjustments to center the pole.";
                AfterCalChange("Knobs calibrated. Start Live View to see how far to turn each knob.");
            } catch (OperationCanceledException) {
                finalMsg = CalFail("you cancelled it.");
            } catch (Exception ex) {
                finalMsg = CalFail(FriendlyError(ex));
            } finally {
                SetCalRunning(false);
                CalCanContinue = false;
                if (finalMsg != null) { CalText = finalMsg; Status = finalMsg; } else CalVisible = false;
                IsBusy = false;
            }
        }

        // ---------- solving ----------

        private ICaptureSolver BuildSolver(bool allowBlind, out CaptureSolverParameter parameter, out CaptureSequence seq) {
            var p = profileService.ActiveProfile;
            var plateSolver = plateSolverFactory.GetPlateSolver(p.PlateSolveSettings);
            var blindSolver = plateSolverFactory.GetBlindSolver(p.PlateSolveSettings);
            var solver = plateSolverFactory.GetCaptureSolver(plateSolver, blindSolver, imagingMediator, filterWheelMediator);

            parameter = new CaptureSolverParameter() {
                Attempts = 1,
                Binning = Binning,
                Coordinates = telescopeMediator.GetCurrentPosition(),
                DownSampleFactor = p.PlateSolveSettings.DownSampleFactor,
                FocalLength = p.TelescopeSettings.FocalLength,
                MaxObjects = p.PlateSolveSettings.MaxObjects,
                PixelSize = p.CameraSettings.PixelSize,
                ReattemptDelay = TimeSpan.FromSeconds(1),
                Regions = p.PlateSolveSettings.Regions,
                SearchRadius = p.PlateSolveSettings.SearchRadius,
                BlindFailoverEnabled = allowBlind && p.PlateSolveSettings.BlindFailoverEnabled,
                DisableNotifications = !allowBlind
            };
            seq = MakeSequence();
            return solver;
        }

        /// <summary>Builds a fresh exposure request from the current Exp / Gain / Bin / Crop settings.</summary>
        private CaptureSequence MakeSequence() {
            var p = profileService.ActiveProfile;
            var seq = new CaptureSequence(ExposureTime, CaptureSequence.ImageTypes.SNAPSHOT, p.PlateSolveSettings.Filter,
                                          new BinningMode(Binning, Binning), 1) { Gain = Gain };
            try {
                var cam = cameraMediator.GetInfo();
                if (CropPercent < 100 && cam.CanSubSample && cam.XSize > 0 && cam.YSize > 0) {
                    int bin = Math.Max(1, (int)Binning);
                    int fullW = cam.XSize / bin, fullH = cam.YSize / bin;   // binned pixels
                    int w = Math.Max(64, (fullW * CropPercent / 100) / 16 * 16);
                    int h = Math.Max(64, (fullH * CropPercent / 100) / 16 * 16);
                    int x = Math.Max(0, (fullW - w) / 2) / 2 * 2;
                    int y = Math.Max(0, (fullH - h) / 2) / 2 * 2;
                    seq.EnableSubSample = true;
                    seq.SubSambleRectangle = new NINA.Core.Utility.ObservableRectangle(x, y, w, h);
                }
            } catch { }
            return seq;
        }

        private async Task<PlateSolveResult> SolveOnce(ICaptureSolver solver, CaptureSolverParameter parameter, CaptureSequence seq, CancellationToken ct) {
            // Refresh the hint each time: the mount (and its reported position) moved since last frame.
            parameter.Coordinates = telescopeMediator.GetCurrentPosition();

            // The frame is never aborted mid-exposure / mid-download and the solve is never cut off: cancelling a running
            // capture makes NINA (or the camera simulator's sky download) pop up its own "Unexpected error" toast.
            // Any stop request only takes effect after this frame has been solved.
            var solveTask = solver.Solve(seq, parameter, new Progress<PlateSolveProgress>(), new Progress<ApplicationStatus>(), CancellationToken.None);
            var solved = await solveTask;
            ct.ThrowIfCancellationRequested();   // a Cancel from the calibration window takes effect only after the frame is done
            return solved;
        }

        private static SolvedFrame ToFrame(PlateSolveResult r) => new SolvedFrame {
            RaDeg = r.Coordinates.RADegrees,   // J2000 (PlateSolveResult always stores J2000)
            DecDeg = r.Coordinates.Dec,
            PositionAngleDeg = r.PositionAngle
        };

        private bool EquipmentReady() {
            if (!cameraMediator.GetInfo().Connected) {
                Status = "Connect a camera first (Equipment tab).";
                return false;
            }
            if (!telescopeMediator.GetInfo().Connected) {
                Status = "Connect the mount first (Equipment tab).";
                return false;
            }
            CheckCalMount();
            return true;
        }

        /// <summary>Turns mount tracking on if it is off (no separate button needed). Returns false only if the mount refuses.</summary>
        private async Task<bool> EnsureTracking() {
            try {
                var info = telescopeMediator.GetInfo();
                if (info.AtPark || info.TrackingEnabled) return true;
                Status = "Turning mount tracking on...";
                telescopeMediator.SetTrackingEnabled(true);
                await Task.Delay(1500);
                if (telescopeMediator.GetInfo().TrackingEnabled) return true;
                Status = "Could not turn tracking on. Enable it on the mount, then try again.";
                return false;
            } catch (Exception ex) { Status = FriendlyError(ex); return false; }
        }

        private static string FriendlyError(Exception ex) {
            if (ex.GetType().Name == "CameraConnectionLostException") return "Camera connection lost. Reconnect the camera and try again.";
            return "Error: " + ex.Message;
        }

        // ---------- commands ----------

        /// <summary>Capture + solve one frame and store it. Returns true if the frame was accepted.</summary>
        private async Task<bool> CaptureCore(CancellationToken ct) {
            Status = $"Capturing + solving frame {frames.Count + 1}...";
            var solver = BuildSolver(true, out var parameter, out var seq);
            var result = await SolveOnce(solver, parameter, seq, ct);
            if (result == null || !result.Success || result.Coordinates == null) {
                Status = "Plate solve failed. Rotate/retry the same position or raise exposure.";
                return false;
            }

            double mountDec = telescopeMediator.GetInfo().Connected ? telescopeMediator.GetInfo().Declination : double.NaN;
            if (frames.Count > 0 && !double.IsNaN(mountDec) && mountDecs.Count > 0 && !double.IsNaN(mountDecs[0])
                && Math.Abs(mountDec - mountDecs[0]) > 0.5) {
                Status = "Mount Dec changed since frame 1. Rotate RA only. Frame not accepted.";
                return false;
            }

            // Reject a frame that barely moved: the axis can't be derived from near-identical positions.
            if (frames.Count > 0) {
                var f = ToFrame(result);
                double sep = PolarMath.AngleDeg(Vec3.FromRaDec(f.RaDeg, f.DecDeg),
                                                Vec3.FromRaDec(frames[frames.Count - 1].RaDeg, frames[frames.Count - 1].DecDeg));
                if (sep < 8.0) {
                    Status = $"Only {sep:0.0}° from previous frame. Rotate RA at least ~20° and recapture.";
                    return false;
                }
            }

            frames.Add(ToFrame(result));
            mountDecs.Add(mountDec);
            FrameCountText = $"Frames: {frames.Count} / 3";

            if (frames.Count < 3) {
                Status = $"Frame {frames.Count} solved.";
                Instructions = "Find Axis is moving the mount in RA for the next frame. Do not touch the mount.";
                return true;
            }

            var solved = PolarMath.SolveAxis(frames, SouthHemisphere);
            bool good = solved.ResidualDeg < 0.1 && solved.RotationDeg >= 15 &&
                        (double.IsNaN(solved.RejectedResidualDeg) || solved.RejectedResidualDeg > 3 * solved.ResidualDeg);
            string check = $"residual {solved.ResidualDeg * 60:0.0}′, rotation {solved.RotationDeg:0}°, PA sign {(solved.PaSign > 0 ? "+" : "-")}";
            NINA.Core.Utility.Logger.Info($"PolarAlignLive axis: residual={solved.ResidualDeg * 60:0.0}' rotation={solved.RotationDeg:0.0} paSign={solved.PaSign} rejectedResidual={solved.RejectedResidualDeg * 60:0.0}' good={good}");

            if (!good) {
                // An unreliable axis must never produce alignment guidance: throw it away and require a fresh capture.
                axis = null;
                frames.Clear();
                mountDecs.Clear();
                HasSolution = false;
                MoveUpArcmin = 0; MoveEastArcmin = 0; TotalErrorArcmin = 0;
                FrameCountText = "Frames: 0 / 3";
                AxisQualityText = "Axis check FAILED (" + check + "). The result was rejected.";
                Status = "Axis rejected: the three frames do not give a reliable axis.";
                Instructions = "No alignment guidance is given from an unreliable axis. Check focus, exposure and plate solving, then press Find Axis again.";
                NotifyCommands();
                return false;
            }

            axis = solved;
            AxisQualityText = "Axis check: " + check + " (good)";
            Status = "Axis found.";
            Instructions = "Press Start Live, then adjust the mount's altitude/azimuth bolts until the arrows reach zero. Do not move RA/Dec now.";
            return true;
        }

        // ---------- automatic RA-only capture ----------

        private async Task<bool> WaitForConfirm(CancellationToken ct) {
            confirmTcs = new TaskCompletionSource<bool>();
            using (ct.Register(() => confirmTcs.TrySetResult(false))) {
                return await confirmTcs.Task;
            }
        }

        private static string RaText(double raDeg) {
            double h = raDeg / 15.0;
            int hh = (int)h; double m = (h - hh) * 60.0;
            return $"{hh:00}h{m:00.0}m";
        }

        private Task AutoCapture() => AutoCaptureCore(false);

        /// <summary>skipConfirm = true when the knob calibration runs Find Axis on the user's behalf (the user already asked
        /// for the calibration; All Stop remains available).</summary>
        private async Task AutoCaptureCore(bool skipConfirm) {
            if (!EquipmentReady()) return;
            var info = telescopeMediator.GetInfo();
            if (info.AtPark) { Status = "Mount is parked. Unpark first."; return; }
            bool trackingWasOff = !info.TrackingEnabled;

            if (frames.Count > 0 || axis != null) Reset();

            var astro = profileService.ActiveProfile.AstrometrySettings;
            var pos = telescopeMediator.GetCurrentPosition().Transform(Epoch.JNOW);
            if (!SlewPlanner.Plan(pos.RADegrees, pos.Dec, astro.Latitude, astro.Longitude, DateTime.UtcNow,
                                  AutoStepDeg, MinAltDeg, MaxAltDeg, out var plan, out var reason)) {
                Status = "Find Axis is not safe here: " + reason;
                Instructions = "No slew was made. Change the mount Dec (e.g. nearer the celestial equator) or adjust the altitude limits, then try again.";
                return;
            }

            cts = new CancellationTokenSource();
            var ct = cts.Token;
            allStop = false;
            // Every movement of this run goes through the gate, so All Stop blocks all of them (including the tracking wait and the reposition slew).
            var gate = new MoveGate(ct);
            moveGate = gate;
            autoRunning = true;
            IsBusy = true;
            try {
                string side = plan.HaSign > 0 ? "west" : "east";
                string path = (plan.NeedsReposition ? RaText(pos.RADegrees) + " > " + RaText(plan.StartRaDeg) + " (safe start) > " : "") +
                              (plan.NeedsReposition ? "" : RaText(pos.RADegrees) + " > ") + RaText(plan.RaDeg[0]) + " > " + RaText(plan.RaDeg[1]);
                bool decMoves = Math.Abs(plan.DecDeg - pos.Dec) > 0.5;
                string decPart = decMoves
                    ? "the mount will FIRST move to a usable sky area (Dec " + pos.Dec.ToString("0", CultureInfo.InvariantCulture) + "° to " + plan.DecDeg.ToString("0", CultureInfo.InvariantCulture) + "°), then slew in RA only"
                    : "the mount will slew in RA only (Dec " + pos.Dec.ToString("0.0", CultureInfo.InvariantCulture) + "° unchanged)";
                ConfirmText = "WARNING: " + decPart + ", staying on the " + side + " side of the meridian: " +
                              path + ". Altitude stays " + plan.MinAltSeen.ToString("0") + "-" + plan.MaxAltSeen.ToString("0") + "°. " +
                              (plan.NeedsReposition ? "The current position is not suitable for the capture sequence, so it will first move to the safe start before any image is taken. " : "") +
                              (trackingWasOff ? "Mount tracking is off and will be switched ON first. " : "") +
                              "Check that cables, scope and pier are clear. SLEW to proceed, CANCEL to stop with no movement.";
                if (!skipConfirm) {
                    Status = "Waiting for your confirmation before any slew.";
                    ConfirmPending = true;
                    bool ok = await WaitForConfirm(ct);
                    ConfirmPending = false;
                    if (!ok) { Status = "Cancelled. The mount was not moved."; return; }
                }

                if (trackingWasOff) {
                    Status = "Turning mount tracking on...";
                    var trk = await gate.StartTrackingAsync(() => telescopeMediator.SetTrackingEnabled(true),
                                                            () => telescopeMediator.GetInfo().TrackingEnabled, 1500);
                    if (trk == MoveOutcome.Stopped) { Status = AllStopDoneText; return; }
                    if (trk != MoveOutcome.Done) {
                        Status = "Could not turn tracking on. Enable it on the mount, then try again. The mount was not moved.";
                        return;
                    }
                }
                if (gate.IsStopped) { Status = AllStopDoneText; return; }

                var pier = telescopeMediator.GetInfo().SideOfPier;

                if (plan.NeedsReposition) {
                    if (!SlewPlanner.Check(plan.StartRaDeg, plan.DecDeg, astro.Latitude, astro.Longitude, DateTime.UtcNow,
                                           plan.HaSign, MinAltDeg, MaxAltDeg, out var why0, out _, out _)) {
                        Status = "Stopped before moving to the safe start: " + why0 + ".";
                        return;
                    }
                    Status = $"Moving to safe start RA {RaText(plan.StartRaDeg)}...";
                    var start = new Coordinates(Angle.ByDegree(plan.StartRaDeg), Angle.ByDegree(plan.DecDeg), Epoch.JNOW);
                    var startRes = await gate.SlewAsync(tok => telescopeMediator.SlewToCoordinatesAsync(start, tok), () => telescopeMediator.StopSlew());
                    if (startRes == MoveOutcome.Stopped) { Status = AllStopDoneText; return; }
                    if (startRes != MoveOutcome.Done) { Status = "Slew did not complete."; return; }
                    if (!telescopeMediator.GetInfo().SideOfPier.Equals(pier)) {
                        Status = "Pier side changed during the slew. Stopped. Check the mount and Reset.";
                        return;
                    }
                    Status = "Settling...";
                    await gate.Delay(3000);
                }
                if (gate.IsStopped) { Status = AllStopDoneText; return; }

                if (!await CaptureCore(ct)) return;

                for (int k = 0; k < 2; k++) {
                    if (gate.IsStopped) { Status = AllStopDoneText; return; }
                    // Re-verify right before each slew with the current clock.
                    if (!SlewPlanner.Check(plan.RaDeg[k], plan.DecDeg, astro.Latitude, astro.Longitude, DateTime.UtcNow,
                                           plan.HaSign, MinAltDeg, MaxAltDeg, out var why, out _, out _)) {
                        Status = "Stopped before slew " + (k + 1) + ": " + why + ".";
                        return;
                    }
                    Status = $"Slewing RA to {RaText(plan.RaDeg[k])} (move {k + 1} of 2)...";
                    var target = new Coordinates(Angle.ByDegree(plan.RaDeg[k]), Angle.ByDegree(plan.DecDeg), Epoch.JNOW);
                    var slewRes = await gate.SlewAsync(tok => telescopeMediator.SlewToCoordinatesAsync(target, tok), () => telescopeMediator.StopSlew());
                    if (slewRes == MoveOutcome.Stopped) { Status = AllStopDoneText; return; }
                    if (slewRes != MoveOutcome.Done) { Status = "Slew did not complete."; return; }
                    if (!telescopeMediator.GetInfo().SideOfPier.Equals(pier)) {
                        Status = "Pier side changed during the slew. Stopped. Check the mount and Reset.";
                        return;
                    }
                    Status = "Settling...";
                    await gate.Delay(3000);
                    if (gate.IsStopped) { Status = AllStopDoneText; return; }
                    if (!await CaptureCore(ct)) return;
                }
            } catch (OperationCanceledException) {
                try { telescopeMediator.StopSlew(); } catch { }
                Status = gate.IsStopped ? AllStopDoneText : "Cancelled. Mount slew stopped.";
            } catch (Exception ex) {
                try { telescopeMediator.StopSlew(); } catch { }
                Status = gate.IsStopped ? AllStopDoneText : FriendlyError(ex);
            } finally {
                ConfirmPending = false;
                autoRunning = false;
                moveGate = null;
                gate.Dispose();
                IsBusy = false;
            }
        }

        private async Task StartLive() {
            if (axis == null) return;
            if (!EquipmentReady() || !await EnsureTracking()) return;
            cts = new CancellationTokenSource();
            var ct = cts.Token;
            liveStop = false;
            liveRunning = true;
            IsBusy = true;
            Instructions = "Adjust altitude/azimuth. Dot = where your RA axis points; bullseye = pole.";
            CalNoteText = CalNote();
            Task<IExposureData> pending = null;
            try {
                var solver = BuildSolver(false, out var parameter, out _);
                var imageSolver = solver.ImageSolver;
                var progress = new Progress<ApplicationStatus>();
                int misses = 0;

                // With no pause, the next exposure starts as soon as the previous frame has downloaded, so exposure +
                // download of frame N+1 overlaps with the plate solve of frame N. With a pause, the next exposure only
                // starts after the pause so the picture is taken after the user's adjustment.
                // Exp / Gain / Bin / Crop are re-read for every new exposure.
                short pendingBin = Binning;

                while (!liveStop && !ct.IsCancellationRequested) {
                    if (pending == null) { pendingBin = Binning; pending = imagingMediator.CaptureImage(MakeSequence(), ct, progress, ""); BeepHandsOff(); }
                    Status = "Exposing...";
                    SetPhase(2, pauseSec <= 0 ? "HANDS OFF \u2013 exposures run continuously (set Pause above 0 to get time to adjust)" : "HANDS OFF \u2013 taking the picture");
                    var exposure = await pending;
                    pending = null;
                    short solveBin = pendingBin;

                    if (pauseSec <= 0) { pendingBin = Binning; pending = imagingMediator.CaptureImage(MakeSequence(), ct, progress, ""); }

                    Status = "Solving...";
                    if (pauseSec > 0) SetPhase(3, "WAIT \u2013 picture taken, solving. The new reading is coming.");
                    var imageData = await exposure.ToImageData(progress, ct);
                    parameter.Binning = solveBin;
                    parameter.Coordinates = telescopeMediator.GetCurrentPosition();
                    var result = await imageSolver.Solve(imageData, parameter, progress, ct);
                    ct.ThrowIfCancellationRequested();   // Stop pressed during the solve: report "stopped", not "solve failed"
                    if (result == null || !result.Success || result.Coordinates == null) {
                        misses++;
                        Status = $"Solve failed ({misses}). Retrying...";
                        continue;
                    }
                    misses = 0;

                    var live = ToFrame(result);
                    var a = PolarMath.AxisOnSky(axis, live);
                    a.ToRaDec(out double aRa, out double aDec);

                    // J2000 -> of date, then compare with the pole of date.
                    var ofDate = new Coordinates(Angle.ByDegree(aRa), Angle.ByDegree(aDec), Epoch.J2000).Transform(Epoch.JNOW);
                    var a2 = profileService.ActiveProfile.AstrometrySettings;
                    PolarMath.PoleError(ofDate.RADegrees, ofDate.Dec, a2.Latitude, DateTime.UtcNow, a2.Longitude,
                                        out double up, out double east, out double total, RefractionNow(a2.Latitude));

                    MoveUpArcmin = up;
                    MoveEastArcmin = east;
                    TotalErrorArcmin = total;
                    HasSolution = true;
                    RaisePropertyChanged(nameof(SouthHemisphere));
                    solveSeq++;
                    NINA.Core.Utility.Logger.Info($"PolarAlignLive live: up={up:0.00}' east={east:0.00}' total={total:0.00}' | alt: {AltAdviceText} | az: {AzAdviceText} | altCal={altCal:0.000} azCal={azCal:0.000}");
                    Beep();
                    Flash();
                    RaisePropertyChanged(nameof(MoveUpText));
                    RaisePropertyChanged(nameof(MoveEastText));
                    RaisePropertyChanged(nameof(TotalErrorText));
                    RaisePropertyChanged(nameof(AltAdviceText));
                    RaisePropertyChanged(nameof(AzAdviceText));
                    string stamp = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                    Status = "Live  (" + stamp + ")";
                    for (int left = pauseSec; left > 0 && !liveStop; left--) {
                        if (left == 2 && pauseSec > 2) BeepWarn();
                        SetPhase(1, left > 2 ? "ADJUST NOW \u2013 " + left + " s left" : "FINISH \u2013 " + left + " s, then hands off");
                        Status = "Live  (" + stamp + ")  -  next solve in " + left + " s, adjust now";
                        for (int tick = 0; tick < 4 && !liveStop; tick++) await Task.Delay(250, ct);
                    }
                }
                ct.ThrowIfCancellationRequested();

                // Stopped by the user: let any exposure already running finish (never abort it mid-exposure), then discard it.
                if (pending != null) {
                    Status = "Stopping Live - finishing the last exposure...";
                    try { await pending; } catch { }
                    pending = null;
                }
                SetPhase(0, "");
                Status = "Live is stopped. Make your adjustment, then press Start Live to continue.";
                Instructions = "Live is stopped. The axis solution is kept - press Start Live to resume.";
            } catch (Exception cex) when (cex is OperationCanceledException || ct.IsCancellationRequested) {
                try { cameraMediator.AbortExposure(); } catch { }
                Status = "Live is stopped. Make your adjustment, then press Start Live to continue.";
                Instructions = "Live is stopped. The axis solution is kept - press Start Live to resume.";
            } catch (Exception ex) {
                try { cameraMediator.AbortExposure(); } catch { }
                Status = FriendlyError(ex);
            } finally {
                // Observe any exception from the abandoned exposure so it is not reported as unobserved.
                if (pending != null) _ = pending.ContinueWith(t => { var ignored = t.Exception; }, TaskScheduler.Default);
                liveRunning = false;
                CalNoteText = "";
                SetPhase(0, "");
                IsBusy = false;
            }
        }

        /// <summary>All Stop (enabled only while Find Axis runs): stops every slew immediately. It does NOT cancel the
        /// exposure / download / plate solve in progress - those finish - and Find Axis then ends before the next move.</summary>
        private void AbortSlew() {
            allStop = true;
            try { moveGate?.Stop(); } catch { }
            try { telescopeMediator.StopSlew(); } catch (Exception ex) { NINA.Core.Utility.Logger.Info("PolarAlignLive all stop: " + ex.Message); }
            try { confirmTcs?.TrySetResult(false); } catch { }
            Status = "ALL STOP - mount stopped. Letting the current exposure and solve finish...";
        }

        private volatile bool liveRunning;
        private volatile bool liveStop;

        private void Stop() {
            if (liveRunning) {
                // Live never cancels an exposure or a solve: it finishes the current ones, shows the result, then stops.
                liveStop = true;
                Status = "Stopping Live - finishing the current exposure and solve...";
                return;
            }
            Status = "Stopping - finishing the current frame...";
            try { cts?.Cancel(); } catch { }
            if (autoRunning) { try { telescopeMediator.StopSlew(); } catch { } }
        }

        private void Reset() {
            frames.Clear();
            mountDecs.Clear();
            axis = null;
            HasSolution = false;
            MoveUpArcmin = 0; MoveEastArcmin = 0; TotalErrorArcmin = 0;
            FrameCountText = "Frames: 0 / 3";
            AxisQualityText = "";
            Status = "Idle";
            Instructions = "Point at a plate-solvable field, then press Find Axis (the mount moves itself).";
            NotifyCommands();
        }
    }
}
