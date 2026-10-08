using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace PolarAlignLive {

    /// <summary>
    /// Shared night-mode / colour settings. One instance is used by the dock panel and by the plugin Options page.
    /// Night mode ON: single-colour UI (Red / Green / White) scaled by a brightness setting.
    /// Night mode OFF: NINA default look (white text, NINA green accent).
    /// Saved to %LOCALAPPDATA%\NINA\PolarAlignLive.settings.
    /// </summary>
    public sealed class NightTheme : INotifyPropertyChanged {

        public static NightTheme Instance { get; } = new NightTheme();

        public event PropertyChangedEventHandler PropertyChanged;

        private static readonly Color NinaGreen = Color.FromRgb(0x00, 0xB8, 0xA9);

        private bool nightOn = true;
        private string colorName = "Red";
        private double dimmer = 0.7;

        private NightTheme() { Load(); }

        public bool NightOn { get => nightOn; set { if (nightOn == value) return; nightOn = value; Changed(); } }
        public bool NightOff { get => !nightOn; set { NightOn = !value; } }

        public bool ColorRed { get => colorName == "Red"; set { if (value) SetColor("Red"); } }
        public bool ColorGreen { get => colorName == "Green"; set { if (value) SetColor("Green"); } }
        public bool ColorWhite { get => colorName == "White"; set { if (value) SetColor("White"); } }

        /// <summary>Brightness 15..100 (percent), for a slider.</summary>
        public double DimmerPercent {
            get => Math.Round(dimmer * 100);
            set { SetDimmer(value / 100.0); }
        }

        public string DimmerText => Math.Round(dimmer * 100).ToString(CultureInfo.InvariantCulture) + "%";

        public void StepDimmer(double delta) => SetDimmer(dimmer + delta);

        private void SetColor(string name) {
            if (colorName == name) return;
            colorName = name;
            Changed();
        }

        private void SetDimmer(double v) {
            v = Math.Round(Math.Max(0.15, Math.Min(1.0, v)), 2);
            if (Math.Abs(v - dimmer) < 0.001) return;
            dimmer = v;
            Changed();
        }

        private Color NightBase() {
            switch (colorName) {
                case "Green": return Color.FromRgb(60, 255, 90);
                case "White": return Color.FromRgb(255, 255, 255);
                default: return Color.FromRgb(255, 40, 40);
            }
        }

        private static Color Scale(Color c, double f) =>
            Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));

        private static Brush Br(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        private Color Tint => nightOn ? Scale(NightBase(), dimmer) : NinaGreen;

        /// <summary>Accent colour for the bullseye display.</summary>
        public Color TintColor => Tint;
        public Brush TextBrush => nightOn ? Br(Tint) : Br(Color.FromRgb(235, 235, 235));
        public Brush DimBrush => nightOn ? Br(Scale(Tint, 0.6)) : Br(Color.FromRgb(150, 150, 150));
        public Brush BorderBrushN => nightOn ? Br(Scale(Tint, 0.55)) : Br(NinaGreen);
        public Brush FillBrush => nightOn ? Br(Scale(Tint, 0.12)) : Br(Color.FromRgb(30, 42, 42));

        private void Changed() {
            foreach (var n in new[] { nameof(NightOn), nameof(NightOff), nameof(ColorRed), nameof(ColorGreen), nameof(ColorWhite),
                                      nameof(DimmerPercent), nameof(DimmerText), nameof(TintColor), nameof(TextBrush),
                                      nameof(DimBrush), nameof(BorderBrushN), nameof(FillBrush) }) {
                Raise(n);
            }
            Save();
        }

        private void Raise([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NINA", "PolarAlignLive.settings");

        private void Load() {
            try {
                if (!File.Exists(SettingsPath)) return;
                foreach (var line in File.ReadAllLines(SettingsPath)) {
                    var kv = line.Split('=');
                    if (kv.Length != 2) continue;
                    if (kv[0] == "night") nightOn = kv[1] == "1";
                    else if (kv[0] == "color" && (kv[1] == "Red" || kv[1] == "Green" || kv[1] == "White")) colorName = kv[1];
                    else if (kv[0] == "dimmer" && double.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                        dimmer = Math.Max(0.15, Math.Min(1.0, d));
                }
            } catch { }
        }

        private void Save() {
            try {
                File.WriteAllLines(SettingsPath, new[] {
                    "night=" + (nightOn ? "1" : "0"),
                    "color=" + colorName,
                    "dimmer=" + dimmer.ToString(CultureInfo.InvariantCulture) });
            } catch { }
        }
    }
}
