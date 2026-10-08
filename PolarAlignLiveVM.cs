using CommunityToolkit.Mvvm.Input;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Equipment.Model;
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
                                ITelescopeMediator telescopeMediator,
                                IImagingMediator imagingMediator,
                                IFilterWheelMediator filterWheelMediator,
                                IPlateSolverFactory plateSolverFactory) : base(profileService) {
            this.profileService = profileService;
            this.telescopeMediator = telescopeMediator;
            this.imagingMediator = imagingMediator;
            this.filterWheelMediator = filterWheelMediator;
            this.plateSolverFactory = plateSolverFactory;

            Title = "Polar Align Live";
            CanClose = false;

            exposureTime = 2.0;
            gain = -1;
            binning = Math.Max((short)1, profileService.ActiveProfile.PlateSolveSettings.Binning);
            status = "Idle";
            instructions = "Point anywhere with a plate-solvable field. Capture frame 1.";

            CaptureFrameCommand = new AsyncRelayCommand(CaptureFrame, () => !IsBusy && axis == null && frames.Count < 3);
            StartLiveCommand = new AsyncRelayCommand(StartLive, () => !IsBusy && axis != null);
            StopCommand = new RelayCommand(Stop, () => IsBusy);
            ResetCommand = new RelayCommand(Reset, () => !IsBusy);
        }

        public ICommand CaptureFrameCommand { get; }
        public ICommand StartLiveCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand ResetCommand { get; }

        // ---------- bindable state ----------

        private double exposureTime;
        public double ExposureTime { get => exposureTime; set { exposureTime = Math.Max(0.1, value); RaisePropertyChanged(); } }

        private int gain;
        public int Gain { get => gain; set { gain = value; RaisePropertyChanged(); } }

        private short binning;
        public short Binning { get => binning; set { binning = Math.Max((short)1, value); RaisePropertyChanged(); } }

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
            seq = new CaptureSequence(ExposureTime, CaptureSequence.ImageTypes.SNAPSHOT, p.PlateSolveSettings.Filter,
                                      new BinningMode(Binning, Binning), 1) { Gain = Gain };
            return solver;
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

        // ---------- commands ----------

        private async Task CaptureFrame() {
            cts = new CancellationTokenSource();
            IsBusy = true;
            try {
                Status = $"Capturing + solving frame {frames.Count + 1}...";
                var solver = BuildSolver(true, out var parameter, out var seq);
                var result = await SolveOnce(solver, parameter, seq, cts.Token);
                if (result == null || !result.Success || result.Coordinates == null) {
                    Status = "Plate solve failed. Rotate/retry the same position or raise exposure.";
                    return;
                }

                double mountDec = telescopeMediator.GetInfo().Connected ? telescopeMediator.GetInfo().Declination : double.NaN;
                if (frames.Count > 0 && !double.IsNaN(mountDec) && mountDecs.Count > 0 && !double.IsNaN(mountDecs[0])
                    && Math.Abs(mountDec - mountDecs[0]) > 0.5) {
                    Status = "Mount Dec changed since frame 1. Rotate RA only. Frame not accepted.";
                    return;
                }

                // Reject a frame that barely moved: the axis can't be derived from near-identical positions.
                if (frames.Count > 0) {
                    var f = ToFrame(result);
                    double sep = PolarMath.AngleDeg(Vec3.FromRaDec(f.RaDeg, f.DecDeg),
                                                    Vec3.FromRaDec(frames[frames.Count - 1].RaDeg, frames[frames.Count - 1].DecDeg));
                    if (sep < 8.0) {
                        Status = $"Only {sep:0.0}° from previous frame. Rotate RA at least ~20° and recapture.";
                        return;
                    }
                }

                frames.Add(ToFrame(result));
                mountDecs.Add(mountDec);
                FrameCountText = $"Frames: {frames.Count} / 3";

                if (frames.Count < 3) {
                    Status = $"Frame {frames.Count} solved.";
                    Instructions = "Rotate the mount in RA only (>= ~20°, more is better; do not touch Dec), then capture the next frame.";
                    return;
                }

                axis = PolarMath.SolveAxis(frames);
                bool good = axis.ResidualDeg < 0.1 && axis.RotationDeg >= 15 &&
                            (double.IsNaN(axis.RejectedResidualDeg) || axis.RejectedResidualDeg > 3 * axis.ResidualDeg);
                AxisQualityText = $"Axis check: residual {axis.ResidualDeg * 60:0.0}′, rotation {axis.RotationDeg:0}°, PA sign {(axis.PaSign > 0 ? "+" : "-")}" +
                                  (good ? " (good)" : " (POOR - consider Reset and use larger RA moves)");
                Status = "Axis found.";
                Instructions = "Press Start Live, then adjust the mount's altitude/azimuth bolts until the arrows reach zero. Do not move RA/Dec now.";
            } catch (OperationCanceledException) {
                Status = "Cancelled.";
            } catch (Exception ex) {
                Status = "Error: " + ex.Message;
            } finally {
                IsBusy = false;
            }
        }

        private async Task StartLive() {
            if (axis == null) return;
            cts = new CancellationTokenSource();
            var ct = cts.Token;
            IsBusy = true;
            Instructions = "Adjust altitude/azimuth. Dot = where your RA axis points; bullseye = pole.";
            try {
                var solver = BuildSolver(false, out var parameter, out var seq);
                int misses = 0;
                while (!ct.IsCancellationRequested) {
                    Status = "Solving...";
                    var result = await SolveOnce(solver, parameter, seq, ct);
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
                Status = "Stopped.";
            } catch (Exception ex) {
                Status = "Error: " + ex.Message;
            } finally {
                IsBusy = false;
            }
        }

        private void Stop() {
            try { cts?.Cancel(); } catch { }
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
            Instructions = "Point anywhere with a plate-solvable field. Capture frame 1.";
            NotifyCommands();
        }
    }
}
