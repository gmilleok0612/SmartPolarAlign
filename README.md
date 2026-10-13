# Polar Align Live (NINA plugin)

Polar Align Pro style bullseye + arrows, driven by plate solves instead of a compass.
Builds on GitHub Actions (no local .NET toolchain needed); NINA interfaces were read from NINA's real source.

## Build / install
1. Open `PolarAlignLive.csproj`, pin the `NINA.*` package versions to your installed NINA (currently wildcard `3.*`).
2. Build Release. Copy `PolarAlignLive.dll` to `%LOCALAPPDATA%\NINA\Plugins\<NINA version folder>\`.
3. Restart NINA. Imaging tab > dock panel list > "Polar Align Live".

## Use
1. Connect camera + mount. Set up plate solver in NINA options. Mount tracking is switched on automatically if it is off.
2. Point anywhere solvable. Polaris is NOT needed. Press Find Axis: after your confirmation the mount slews in RA only
   and takes three frames; the plugin derives the RA axis and checks it.
3. (Optional) Settings > pick each knob's unit (tics / turns / degrees) > Calibrate Adjustment Knobs and follow the window.
4. Start Live. Adjust the mount's alt/az bolts until both arrows read OK. Dot = your RA axis, bullseye = pole of date.
   Arrows/text say which way to move the AXIS (raise/lower, swing east/west, as seen facing the pole - facing south in the southern hemisphere, where the bullseye is mirrored so east is on the left).
5. Stop. When the error is under 3' the panel says "ALIGNMENT GOOD ENOUGH FOR GUIDING"; under 1' it says "ALIGNMENT EXCELLENT".

## Phone access (optional)
Settings > "Phone access: On". The plugin runs a small web server (default port 5050, PIN shown in Settings). On a phone on the same
Wi-Fi open `http://<NINA PC IP>:5050`, enter the PIN, and use the same buttons, bullseye and calibration as the panel.
Allow the port through Windows Firewall. Local network only.

## How it works
- Each solve gives center RA/Dec + position angle. Three frames at three RA positions give the RA axis
  (all fixed camera points move perpendicular to it). Frame 3 auto-detects the solver's position-angle sign.
- The axis is stored in camera coordinates, so every live solve converts it back to a sky position,
  to JNow, to alt/az at your profile location, and compares with the pole (alt = latitude, az = 0).

## Verified (Python mirror of the math, see verify/)
- Axis recovered to ~0.005 deg with ~7" of solve noise; wrong PA sign rejected (resid 0.23 deg vs <0.005).
- Alt/az error signs and magnitudes check out (polar distance = hypot of the two components).
- Image mirroring cancels out and is irrelevant.

## Known limits
- Works in both hemispheres (the sign of the NINA profile latitude decides: negative = south celestial pole). Needs profile latitude/longitude correct and PC clock correct. The southern hemisphere has been checked by calculation only, not yet on a southern sky - testers welcome.
- Live rate = exposure + solve time per frame (seconds, not video rate).
- Dec must not move between frames 1-3 (rejected if mount reports >0.5 deg Dec change).
- Pause s (Settings, PC and phone): seconds to wait after each Live update before the next exposure (default 5, range 0-60), so you have time to turn the knobs. 0 = continuous.
- Live beep and Live flash (Settings): each new Live solution beeps and briefly brightens the advice text, on the PC and on the phone page. Both can be turned off in Settings; the phone page has its own beep switch too.
- Refraction: Settings > "Refraction: On" (default) aims at the apparent pole, which sits about 1' above the true pole at mid latitudes (the bend of starlight by the atmosphere, scaled for your site elevation). Turn it off to aim at the true pole.
- Settings (including knob calibration) are saved in %LOCALAPPDATA%\NINA\PolarAlignLive.auto.settings.

## No local build tools? Build on GitHub (free)
1. Create a private GitHub repo, upload the contents of this folder (including `.github/`).
2. Actions tab > "build" > Run workflow (it also runs on push).
3. Download the `PolarAlignLive-plugin` artifact; copy `PolarAlignLive.dll` into the NINA plugins folder.
If the build fails, send me the error log; compile fixes are expected on first build.
