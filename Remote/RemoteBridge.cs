using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;

namespace PolarAlignLive.Remote {

    /// <summary>Reads the view-model's state for the phone page and runs the phone's button presses as the same commands the panel uses.
    /// Everything touching the view-model runs on the UI thread.</summary>
    internal sealed class RemoteBridge {

        private readonly PolarAlignLiveVM vm;
        public RemoteBridge(PolarAlignLiveVM vm) { this.vm = vm; }

        private static T OnUi<T>(Func<T> f) {
            var d = Application.Current?.Dispatcher;
            if (d == null) return default(T);
            return d.Invoke(f);
        }

        private static bool Can(ICommand c) { try { return c != null && c.CanExecute(null); } catch { return false; } }

        public string BuildState() {
            return OnUi(() => {
                var s = new Dictionary<string, object> {
                    ["status"] = vm.Status ?? "",
                    ["instructions"] = vm.Instructions ?? "",
                    ["instructionsShown"] = vm.InstructionsShown,
                    ["frames"] = vm.FrameCountText ?? "",
                    ["axisCheck"] = vm.AxisQualityText ?? "",
                    ["hasSolution"] = vm.HasSolution,
                    ["moveUp"] = vm.MoveUpArcmin,
                    ["moveEast"] = vm.MoveEastArcmin,
                    ["total"] = vm.TotalErrorArcmin,
                    ["altLine"] = vm.MoveUpText ?? "",
                    ["altAdvice"] = vm.AltAdviceText ?? "",
                    ["azLine"] = vm.MoveEastText ?? "",
                    ["azAdvice"] = vm.AzAdviceText ?? "",
                    ["totalText"] = vm.TotalErrorText ?? "",
                    ["quality"] = vm.AlignmentLevel,
                    ["qualityText"] = vm.AlignmentQualityText ?? "",
                    ["busy"] = vm.IsBusy,
                    ["confirmPending"] = vm.ConfirmPending,
                    ["confirmText"] = vm.ConfirmText ?? "",
                    ["confirmYes"] = vm.ConfirmYesText ?? "SLEW",
                    ["calVisible"] = vm.CalVisible,
                    ["calText"] = vm.CalText ?? "",
                    ["calCanContinue"] = vm.CalCanContinue,
                    ["calCancelText"] = vm.CalCancelText ?? "Cancel",
                    ["exp"] = vm.ExposureTime,
                    ["gain"] = vm.Gain,
                    ["bin"] = (int)vm.Binning,
                    ["crop"] = vm.CropPercent,
                    ["pause"] = vm.PauseSeconds,
                    ["altUnit"] = vm.AltUnitText ?? "",
                    ["azUnit"] = vm.AzUnitText ?? "",
                    ["south"] = vm.SouthHemisphere,
                    ["canAuto"] = Can(vm.AutoCaptureCommand),
                    ["canAllStop"] = Can(vm.AbortSlewCommand),
                    ["canLive"] = Can(vm.StartLiveCommand),
                    ["canStop"] = Can(vm.StopCommand),
                    ["canReset"] = Can(vm.ResetCommand),
                    ["beep"] = vm.BeepOn,
                    ["flash"] = vm.FlashOn,
                    ["solveSeq"] = vm.SolveSeq
                };
                return JsonSerializer.Serialize(s);
            }) ?? "{}";
        }

        public string RunCommand(string name, string arg) {
            return OnUi(() => {
                try {
                    ICommand c = null;
                    switch (name) {
                        case "autocapture": c = vm.AutoCaptureCommand; break;
                        case "startlive": c = vm.StartLiveCommand; break;
                        case "stop": c = vm.StopCommand; break;
                        case "reset": c = vm.ResetCommand; break;
                        case "abortslew": c = vm.AbortSlewCommand; break;
                        case "confirm": c = vm.ConfirmSlewCommand; break;
                        case "cancelconfirm": c = vm.CancelSlewCommand; break;
                        case "altunit": c = vm.CycleAltUnitCommand; break;
                        case "azunit": c = vm.CycleAzUnitCommand; break;
                        case "calibrate": c = vm.CalibrateKnobsCommand; break;
                        case "calcontinue": c = vm.CalContinueCommand; break;
                        case "calcancel": c = vm.CalCancelCommand; break;
                        case "exp": {
                            if (double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out var e)) vm.ExposureTime = e;
                            return "ok";
                        }
                        case "gain": {
                            if (int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var g)) vm.Gain = g;
                            return "ok";
                        }
                        case "bin": {
                            if (short.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b)) vm.Binning = b;
                            return "ok";
                        }
                        case "pause": {
                            if (int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ps)) vm.PauseSeconds = ps;
                            return "ok";
                        }
                        case "crop": {
                            if (int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)) vm.CropPercent = p;
                            return "ok";
                        }
                        default: return "unknown command";
                    }
                    if (Can(c)) c.Execute(null);
                    return "ok";
                } catch (Exception ex) { return ex.Message; }
            }) ?? "no ui";
        }
    }
}
