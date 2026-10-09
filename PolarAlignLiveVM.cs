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
            instructions = "Point at a plate-solvable field, then press Auto Capture (mount moves itself) or Capture Frame (you move it).";

            CaptureFrameCommand = new AsyncRelayCommand(CaptureFrame, () => !IsBusy && axis == null && frames.Count < 3);
            StartLiveCommand = new AsyncRelayCommand(StartLive, () => !IsBusy && axis != null);
            StopCommand = new RelayCommand(Stop, () => IsBusy);
            ResetCommand = new RelayCommand(Reset, () => !IsBusy);
            AutoCaptureCommand = new AsyncRelayCommand(AutoCapture, () => !IsBusy && axis == null);
            TrackingOnCommand = new RelayCommand(() => {
                try {
                    if (!telescopeMediator.GetInfo().Connected) { Status = "Connect the mount first (Equipment tab)."; return; }
                    bool ok = telescopeMediator.SetTrackingEnabled(true);
                    Status = ok ? "Tracking turned on." : "The mount did not accept the tracking command. Turn tracking on at the mount.";
                } catch (Exception ex) { Status = FriendlyError(ex); }
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
        }

        public ICommand CaptureFrameCommand { get; }
        public ICommand StartLiveCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand ResetCommand { get; }
        public ICommand AutoCaptureCommand { get; }
        public ICommand TrackingOnCommand { get; }
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
                if ((visibilitySynced && iconSet) || tries >= 8) timer.Stop();
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
                    if (kv.Length != 2 || !double.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) continue;
                    if (kv[0] == "step") autoStepDeg = Math.Max(15, Math.Min(40, v));
                    else if (kv[0] == "minalt") minAltDeg = Math.Max(5, Math.Min(80, v));
                    else if (kv[0] == "maxalt") maxAltDeg = Math.Max(20, Math.Min(85, v));
                    else if (kv[0] == "crop") cropPercent = v == 50 ? 50 : v == 25 ? 25 : 100;
                    else if (kv[0] == "settings") settingsOpen = v == 1;
                }
            } catch { }
        }

        private void SaveAutoSettings() {
            try {
                System.IO.File.WriteAllLines(AutoSettingsPath, new[] {
                    "step=" + autoStepDeg.ToString(CultureInfo.InvariantCulture),
                    "minalt=" + minAltDeg.ToString(CultureInfo.InvariantCulture),
                    "maxalt=" + maxAltDeg.ToString(CultureInfo.InvariantCulture),
                    "crop=" + cropPercent.ToString(CultureInfo.InvariantCulture),
                    "settings=" + (settingsOpen ? "1" : "0") });
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
        public string ConfirmText { get => confirmText; private set { confirmText = value; RaisePropertyChanged(); } }

        private TaskCompletionSource<bool> confirmTcs;
        private bool autoRunning;

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
        public bool HasSolution { get => hasSolution; private set { hasSolution = value; RaisePropertyChanged(); } }

        private double moveUpArcmin;
        public double MoveUpArcmin { get => moveUpArcmin; private set { moveUpArcmin = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(MoveUpText)); } }

        private double moveEastArcmin;
        public double MoveEastArcmin { get => moveEastArcmin; private set { moveEastArcmin = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(MoveEastText)); } }

        private double totalErrorArcmin;
        public double TotalErrorArcmin { get => totalErrorArcmin; private set { totalErrorArcmin = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(TotalErrorText)); } }

        public string MoveUpText => !HasSolution ? "ALT  --" :
            (Math.Abs(MoveUpArcmin) < 0.5 ? "ALT  OK" : $"ALT  {(MoveUpArcmin > 0 ? "RAISE axis" : "LOWER axis")}  {Fmt(Math.Abs(MoveUpArcmin))}");

        public string MoveEastText => !HasSolution ? "AZ  --" :
            (Math.Abs(MoveEastArcmin) < 0.5 ? "AZ  OK" : $"AZ  swing axis {(MoveEastArcmin > 0 ? "EAST" : "WEST")}  {Fmt(Math.Abs(MoveEastArcmin))}");

        public string TotalErrorText => !HasSolution ? "Polar error  --" : $"Polar error  {Fmt(TotalErrorArcmin)}";

        private static string Fmt(double arcmin) =>
            arcmin >= 60 ? (arcmin / 60.0).ToString("0.00", CultureInfo.InvariantCulture) + "°"
                         : arcmin.ToString("0.0", CultureInfo.InvariantCulture) + "′";

        private void NotifyCommands() {
            // Always called from UI-context continuations or UI commands.
            (CaptureFrameCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
            (AutoCaptureCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
            (StartLiveCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
            (StopCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (ResetCommand as RelayCommand)?.NotifyCanExecuteChanged();
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
            return await solver.Solve(seq, parameter, new Progress<PlateSolveProgress>(), new Progress<ApplicationStatus>(), ct);
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
            return true;
        }

        private static string FriendlyError(Exception ex) {
            if (ex.GetType().Name == "CameraConnectionLostException") return "Camera connection lost. Reconnect the camera and try again.";
            return "Error: " + ex.Message;
        }

        // ---------- commands ----------

        private async Task CaptureFrame() {
            if (!EquipmentReady()) return;
            cts = new CancellationTokenSource();
            IsBusy = true;
            try {
                await CaptureCore(cts.Token);
            } catch (OperationCanceledException) {
                Status = "Cancelled.";
            } catch (Exception ex) {
                Status = FriendlyError(ex);
            } finally {
                IsBusy = false;
            }
        }

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
                Instructions = "Rotate the mount in RA only (>= ~20°, do not touch Dec), then press Capture Frame (manual). Or press Reset and use Auto Capture to let the mount move itself.";
                return true;
            }

            axis = PolarMath.SolveAxis(frames);
            bool good = axis.ResidualDeg < 0.1 && axis.RotationDeg >= 15 &&
                        (double.IsNaN(axis.RejectedResidualDeg) || axis.RejectedResidualDeg > 3 * axis.ResidualDeg);
            AxisQualityText = $"Axis check: residual {axis.ResidualDeg * 60:0.0}′, rotation {axis.RotationDeg:0}°, PA sign {(axis.PaSign > 0 ? "+" : "-")}" +
                              (good ? " (good)" : " (POOR - consider Reset and use larger RA moves)");
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

        private async Task AutoCapture() {
            if (!EquipmentReady()) return;
            var info = telescopeMediator.GetInfo();
            if (info.AtPark) { Status = "Mount is parked. Unpark first."; return; }
            bool trackingWasOff = !info.TrackingEnabled;

            if (frames.Count > 0 || axis != null) Reset();

            var astro = profileService.ActiveProfile.AstrometrySettings;
            var pos = telescopeMediator.GetCurrentPosition().Transform(Epoch.JNOW);
            if (!SlewPlanner.Plan(pos.RADegrees, pos.Dec, astro.Latitude, astro.Longitude, DateTime.UtcNow,
                                  AutoStepDeg, MinAltDeg, MaxAltDeg, out var plan, out var reason)) {
                Status = "Auto capture not safe here: " + reason;
                Instructions = "No slew was made. Change the mount Dec (e.g. nearer the celestial equator) or adjust the altitude limits, then try again.";
                return;
            }

            cts = new CancellationTokenSource();
            var ct = cts.Token;
            IsBusy = true;
            autoRunning = true;
            try {
                string side = plan.HaSign > 0 ? "west" : "east";
                string path = (plan.NeedsReposition ? RaText(pos.RADegrees) + " > " + RaText(plan.StartRaDeg) + " (safe start) > " : "") +
                              (plan.NeedsReposition ? "" : RaText(pos.RADegrees) + " > ") + RaText(plan.RaDeg[0]) + " > " + RaText(plan.RaDeg[1]);
                ConfirmText = "WARNING: the mount will slew in RA only (Dec " + pos.Dec.ToString("0.0", CultureInfo.InvariantCulture) + "° unchanged), staying on the " + side + " side of the meridian: " +
                              path + ". Altitude stays " + plan.MinAltSeen.ToString("0") + "-" + plan.MaxAltSeen.ToString("0") + "°. " +
                              (plan.NeedsReposition ? "The current position is not safe for the capture sequence, so it will first move to the safe start. " : "") +
                              (trackingWasOff ? "Mount tracking is off and will be switched ON first. " : "") +
                              "Check that cables, scope and pier are clear. SLEW to proceed, CANCEL to stop with no movement.";
                Status = "Waiting for your confirmation before any slew.";
                ConfirmPending = true;
                bool ok = await WaitForConfirm(ct);
                ConfirmPending = false;
                if (!ok) { Status = "Cancelled. The mount was not moved."; return; }

                if (trackingWasOff) {
                    Status = "Turning mount tracking on...";
                    bool on = telescopeMediator.SetTrackingEnabled(true);
                    await Task.Delay(1500, ct);
                    if (!on || !telescopeMediator.GetInfo().TrackingEnabled) {
                        Status = "Could not turn tracking on. Enable it on the mount, then try again. The mount was not moved.";
                        return;
                    }
                }

                var pier = telescopeMediator.GetInfo().SideOfPier;

                if (plan.NeedsReposition) {
                    if (!SlewPlanner.Check(plan.StartRaDeg, plan.DecDeg, astro.Latitude, astro.Longitude, DateTime.UtcNow,
                                           plan.HaSign, MinAltDeg, MaxAltDeg, out var why0, out _, out _)) {
                        Status = "Stopped before moving to the safe start: " + why0 + ".";
                        return;
                    }
                    Status = $"Moving to safe start RA {RaText(plan.StartRaDeg)}...";
                    var start = new Coordinates(Angle.ByDegree(plan.StartRaDeg), Angle.ByDegree(plan.DecDeg), Epoch.JNOW);
                    if (!await telescopeMediator.SlewToCoordinatesAsync(start, ct)) { Status = "Slew did not complete."; return; }
                    if (!telescopeMediator.GetInfo().SideOfPier.Equals(pier)) {
                        Status = "Pier side changed during the slew. Stopped. Check the mount and Reset.";
                        return;
                    }
                    Status = "Settling...";
                    await Task.Delay(3000, ct);
                }

                if (!await CaptureCore(ct)) return;

                for (int k = 0; k < 2; k++) {
                    // Re-verify right before each slew with the current clock.
                    if (!SlewPlanner.Check(plan.RaDeg[k], plan.DecDeg, astro.Latitude, astro.Longitude, DateTime.UtcNow,
                                           plan.HaSign, MinAltDeg, MaxAltDeg, out var why, out _, out _)) {
                        Status = "Stopped before slew " + (k + 1) + ": " + why + ".";
                        return;
                    }
                    Status = $"Slewing RA to {RaText(plan.RaDeg[k])} (move {k + 1} of 2)...";
                    var target = new Coordinates(Angle.ByDegree(plan.RaDeg[k]), Angle.ByDegree(plan.DecDeg), Epoch.JNOW);
                    bool done = await telescopeMediator.SlewToCoordinatesAsync(target, ct);
                    if (!done) { Status = "Slew did not complete."; return; }
                    if (!telescopeMediator.GetInfo().SideOfPier.Equals(pier)) {
                        Status = "Pier side changed during the slew. Stopped. Check the mount and Reset.";
                        return;
                    }
                    Status = "Settling...";
                    await Task.Delay(3000, ct);
                    if (!await CaptureCore(ct)) return;
                }
            } catch (OperationCanceledException) {
                try { telescopeMediator.StopSlew(); } catch { }
                Status = "Cancelled. Mount slew stopped.";
            } catch (Exception ex) {
                try { telescopeMediator.StopSlew(); } catch { }
                Status = FriendlyError(ex);
            } finally {
                ConfirmPending = false;
                autoRunning = false;
                IsBusy = false;
            }
        }

        private async Task StartLive() {
            if (axis == null) return;
            if (!EquipmentReady()) return;
            cts = new CancellationTokenSource();
            var ct = cts.Token;
            IsBusy = true;
            Instructions = "Adjust altitude/azimuth. Dot = where your RA axis points; bullseye = pole.";
            Task<IExposureData> pending = null;
            try {
                var solver = BuildSolver(false, out var parameter, out _);
                var imageSolver = solver.ImageSolver;
                var progress = new Progress<ApplicationStatus>();
                int misses = 0;

                // Pipeline: the next exposure starts as soon as the previous frame has downloaded,
                // so exposure + download of frame N+1 overlaps with the plate solve of frame N.
                // Exp / Gain / Bin / Crop are re-read for every new exposure.
                short pendingBin = Binning;
                pending = imagingMediator.CaptureImage(MakeSequence(), ct, progress, "");

                while (!ct.IsCancellationRequested) {
                    Status = "Exposing...";
                    var exposure = await pending;
                    short solveBin = pendingBin;

                    pendingBin = Binning;
                    pending = imagingMediator.CaptureImage(MakeSequence(), ct, progress, "");

                    Status = "Solving...";
                    var imageData = await exposure.ToImageData(progress, ct);
                    parameter.Binning = solveBin;
                    parameter.Coordinates = telescopeMediator.GetCurrentPosition();
                    var result = await imageSolver.Solve(imageData, parameter, progress, ct);
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
                                        out double up, out double east, out double total);

                    MoveUpArcmin = up;
                    MoveEastArcmin = east;
                    TotalErrorArcmin = total;
                    HasSolution = true;
                    RaisePropertyChanged(nameof(MoveUpText));
                    RaisePropertyChanged(nameof(MoveEastText));
                    RaisePropertyChanged(nameof(TotalErrorText));
                    Status = "Live  (" + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + ")";
                }
            } catch (OperationCanceledException) {
                try { cameraMediator.AbortExposure(); } catch { }
                Status = "Stopped.";
            } catch (Exception ex) {
                try { cameraMediator.AbortExposure(); } catch { }
                Status = FriendlyError(ex);
            } finally {
                // Observe any exception from the abandoned exposure so it is not reported as unobserved.
                if (pending != null) _ = pending.ContinueWith(t => { var ignored = t.Exception; }, TaskScheduler.Default);
                IsBusy = false;
            }
        }

        private void Stop() {
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
            Instructions = "Point at a plate-solvable field, then press Auto Capture (mount moves itself) or Capture Frame (you move it).";
            NotifyCommands();
        }
    }
}
