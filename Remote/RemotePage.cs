namespace PolarAlignLive.Remote {

    /// <summary>The phone web page (generated from Remote/index.html).</summary>
    internal static class RemotePage {
        public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1, user-scalable=no, viewport-fit=cover">
<meta name="apple-mobile-web-app-capable" content="yes">
<meta name="mobile-web-app-capable" content="yes">
<meta name="apple-mobile-web-app-status-bar-style" content="black">
<meta name="theme-color" content="#000000">
<title>Polar Align Live</title>
<style>
  :root { --c: #b01818; --dim: #6a0f0f; --bg: #000; }
  * { box-sizing: border-box; -webkit-tap-highlight-color: transparent; }
  html, body { margin: 0; background: var(--bg); color: var(--c); font-family: -apple-system, "Segoe UI", Roboto, Arial, sans-serif; }
  body { padding: env(safe-area-inset-top) 12px env(safe-area-inset-bottom) 12px; max-width: 640px; margin: 0 auto; }
  .row { display: flex; flex-wrap: wrap; gap: 8px; margin: 8px 0; align-items: center; }
  button { font: inherit; font-size: 17px; color: var(--c); background: #140404; border: 2px solid var(--c); border-radius: 8px; padding: 12px 10px; min-height: 48px; flex: 1 1 30%; }
  button:disabled { color: var(--dim); border-color: var(--dim); opacity: .55; }
  button.on { background: #2a0808; box-shadow: inset 0 0 0 2px var(--c); }
  button.big { flex: 1 1 100%; font-weight: 700; letter-spacing: 1px; }
  button.small { flex: 0 0 auto; min-height: 40px; padding: 8px 14px; }
  input { font: inherit; font-size: 17px; color: var(--c); background: #000; border: 2px solid var(--dim); border-radius: 6px; padding: 8px; width: 78px; }
  label { color: var(--dim); font-size: 14px; display: flex; align-items: center; gap: 6px; }
  .instr { font-size: 16px; margin: 6px 0 2px; }
  .frames { font-size: 13px; color: var(--dim); }
  .line { font-size: 24px; font-weight: 700; margin: 6px 0 0; transition: filter .6s ease-out; }
  .adv { font-size: 18px; font-weight: 700; margin: 0 0 2px; transition: filter .6s ease-out; }
  .flash { filter: brightness(2.4) drop-shadow(0 0 8px currentColor); transition: none !important; }
  .err { color: var(--dim); font-size: 17px; text-align: right; }
  .verdict { font-weight: 700; font-size: 16px; text-align: right; }
  .status { color: var(--dim); font-size: 15px; margin: 6px 0; min-height: 22px; word-break: break-word; }
  canvas { display: block; width: 100%; background: #000; margin: 4px auto; }
  .panel { border: 3px solid var(--c); border-radius: 8px; padding: 12px; margin: 8px 0; background: #000; }
  .panel p { font-size: 19px; font-weight: 700; margin: 0 0 8px; white-space: pre-wrap; }
  #cal { position: fixed; inset: 0; background: #000e; z-index: 10; display: none; padding: calc(env(safe-area-inset-top) + 16px) 16px 16px; overflow: auto; }
  #cal .panel { margin-top: 10vh; }
  #cal p { font-size: 22px; }
  #pin { position: fixed; inset: 0; background: #000; z-index: 20; display: none; padding: 20vh 24px; }
  #pin input { width: 100%; font-size: 28px; text-align: center; margin: 12px 0; }
  .hidden { display: none !important; }
  .dot { width: 10px; height: 10px; border-radius: 50%; display: inline-block; background: var(--dim); margin-right: 6px; }
  .dot.ok { background: var(--c); }
  h1 { font-size: 15px; font-weight: 600; margin: 8px 0 0; display: flex; justify-content: space-between; color: var(--dim); }
  hr { border: 0; border-top: 1px solid var(--dim); margin: 10px 0; }
</style>
</head>
<body>
<h1><span><span id="dot" class="dot"></span>Polar Align Live</span><span id="conn">connecting...</span></h1>

<div id="top">
  <div class="instr" id="instr"></div>
  <div class="frames" id="frames"></div>
</div>

<div class="line" id="altLine">ALT  --</div><div class="adv" id="altAdv"></div>
<div class="line" id="azLine">AZ  --</div><div class="adv" id="azAdv"></div>
<div class="err" id="err"></div>
<div class="verdict" id="verdict"></div>

<canvas id="cv" width="800" height="800"></canvas>

<div id="confirm" class="panel hidden">
  <p id="confirmText"></p>
  <div class="row"><button id="confirmYes">SLEW</button><button id="confirmNo">CANCEL</button></div>
</div>

<div class="status" id="status"></div>

<div class="row">
  <button id="bLive">Start Live</button>
  <button id="bStop">Stop Live</button>
  <button id="bAuto">Auto Capture</button>
  <button id="bReset">Reset</button>
</div>
<div class="row"><button class="big" id="bAbort">ABORT SLEW</button></div>

<div class="row"><button class="small" id="bSettings">Settings &#9662;</button></div>
<div id="settings" class="hidden">
  <div class="row">
    <label>Exp (s) <input id="iExp" inputmode="decimal"></label>
    <label>Gain <input id="iGain" inputmode="numeric"></label>
    <label>Bin <input id="iBin" inputmode="numeric"></label>
  </div>
  <div class="row">
    <label>Crop</label>
    <button class="small" data-crop="100">100%</button>
    <button class="small" data-crop="50">50%</button>
    <button class="small" data-crop="25">25%</button>
  </div>
  <div class="row">
    <button id="bAltUnit">Alt knob</button>
    <button id="bAzUnit">Az knob</button>
  </div>
  <div class="row"><button class="big" id="bCal">Calibrate Adjustment Knobs</button></div>
  <hr>
  <div class="row">
    <label>Color</label>
    <button class="small" data-col="red">Red</button>
    <button class="small" data-col="green">Green</button>
    <button class="small" data-col="white">White</button>
  </div>
  <div class="row">
    <label>Brightness</label>
    <button class="small" id="bDimDn">&minus;</button>
    <span id="dimTxt" style="min-width:48px;text-align:center"></span>
    <button class="small" id="bDimUp">+</button>
  </div>
  <div class="row">
    <button id="bSound">Phone beep: Off</button>
    <button id="bAwake">Keep screen on: Off</button>
  </div>
</div>

<div id="cal">
  <div class="panel">
    <p id="calText"></p>
    <div class="row"><button id="calContinue" class="hidden">Knob adjustments complete.</button><button id="calCancel">Cancel</button></div>
  </div>
</div>

<div id="pin">
  <div style="font-size:20px">Enter the access PIN shown in Polar Align Live settings on the NINA PC.</div>
  <input id="pinIn" inputmode="numeric" autocomplete="off">
  <button class="big" id="pinOk">Connect</button>
  <div id="pinMsg" class="status"></div>
</div>

<script>
(function () {
  var $ = function (id) { return document.getElementById(id); };
  function ls(k, v) { try { if (v === undefined) return localStorage.getItem(k); localStorage.setItem(k, v); } catch (e) {} return null; }

  var pin = ls('pal.pin') || '';
  var colName = ls('pal.col') || 'red';
  var dim = parseFloat(ls('pal.dim') || '0.7');
  var soundOn = ls('pal.sound') !== '0';
  var awakeOn = ls('pal.awake') === '1';
  var st = null, lastSeq = -1, failures = 0, audio = null, wake = null;
  var settingsOpen = ls('pal.settings') === '1';

  // ---------- night colours ----------
  var BASE = { red: [224, 43, 43], green: [60, 220, 90], white: [235, 235, 235] };
  function rgb(f) { var b = BASE[colName] || BASE.red; return 'rgb(' + Math.round(b[0] * f) + ',' + Math.round(b[1] * f) + ',' + Math.round(b[2] * f) + ')'; }
  function applyTheme() {
    dim = Math.max(0.15, Math.min(1, dim));
    var r = document.documentElement.style;
    r.setProperty('--c', rgb(dim));
    r.setProperty('--dim', rgb(dim * 0.55));
    $('dimTxt').textContent = Math.round(dim * 100) + '%';
    ls('pal.col', colName); ls('pal.dim', String(dim));
    var cols = document.querySelectorAll('[data-col]');
    for (var i = 0; i < cols.length; i++) cols[i].classList.toggle('on', cols[i].getAttribute('data-col') === colName);
    draw();
  }

  // ---------- network ----------
  function api(path, opts) {
    opts = opts || {};
    opts.headers = { 'X-Pin': pin };
    opts.cache = 'no-store';
    return fetch(path, opts);
  }
  function cmd(name, arg) {
    return api('/api/cmd', { method: 'POST', body: name + '|' + (arg === undefined ? '' : arg) }).then(function (r) {
      if (r.status === 401) { showPin(''); }
      return r.text();
    }).then(poll).catch(function () {});
  }

  function showPin(msg) { $('pin').style.display = 'block'; $('pinMsg').textContent = msg || ''; }

  var polling = false;
  function poll() {
    if (polling) return Promise.resolve();
    polling = true;
    return api('/api/state').then(function (r) {
      if (r.status === 401) { showPin(pin ? 'Wrong PIN.' : ''); throw new Error('401'); }
      return r.json();
    }).then(function (j) {
      failures = 0; st = j; $('pin').style.display = 'none'; render();
    }).catch(function (e) {
      failures++;
      if (failures >= 2) { $('conn').textContent = 'disconnected'; $('dot').className = 'dot'; }
    }).then(function () { polling = false; });
  }
  setInterval(poll, 700);

  // ---------- rendering ----------
  function setText(id, t) { var e = $(id); if (e.textContent !== t) e.textContent = t; }

  function render() {
    if (!st) return;
    $('conn').textContent = 'connected';
    $('dot').className = 'dot ok';
    $('top').classList.toggle('hidden', !st.instructionsShown);
    setText('instr', st.instructions || '');
    setText('frames', (st.frames || '') + (st.axisCheck ? '   ' + st.axisCheck : ''));
    setText('altLine', st.altLine); setText('altAdv', st.altAdvice || '');
    setText('azLine', st.azLine); setText('azAdv', st.azAdvice || '');
    $('altAdv').classList.toggle('hidden', !st.altAdvice);
    $('azAdv').classList.toggle('hidden', !st.azAdvice);
    setText('err', st.totalText);
    setText('verdict', st.qualityText || '');
    setText('status', st.status || '');

    $('confirm').classList.toggle('hidden', !st.confirmPending);
    setText('confirmText', st.confirmText || '');
    setText('confirmYes', st.confirmYes || 'SLEW');

    $('cal').style.display = st.calVisible ? 'block' : 'none';
    setText('calText', st.calText || '');
    $('calContinue').classList.toggle('hidden', !st.calCanContinue);
    setText('calCancel', st.calCancelText || 'Cancel');

    $('bAuto').disabled = !st.canAuto;
    $('bLive').disabled = !st.canLive;
    $('bStop').disabled = !st.canStop;
    $('bReset').disabled = !st.canReset;
    $('bCal').disabled = !!st.busy && !st.calVisible;
    setText('bAltUnit', st.altUnit); setText('bAzUnit', st.azUnit);

    var act = document.activeElement && document.activeElement.id;
    if (act !== 'iExp') $('iExp').value = st.exp;
    if (act !== 'iGain') $('iGain').value = st.gain;
    if (act !== 'iBin') $('iBin').value = st.bin;
    var cs = document.querySelectorAll('[data-crop]');
    for (var i = 0; i < cs.length; i++) cs[i].classList.toggle('on', parseInt(cs[i].getAttribute('data-crop'), 10) === st.crop);

    if (st.solveSeq !== lastSeq) {
      if (lastSeq !== -1 && st.solveSeq > lastSeq) {
        if (soundOn && st.beep !== false) { beep(); try { if (navigator.vibrate) navigator.vibrate(60); } catch (e) {} }
        if (st.flash !== false) flash();
      }
      lastSeq = st.solveSeq;
    }
    updToggles();
    draw();
  }

  // ---------- bullseye (port of PolarDisplay) ----------
  function niceCeil(v) { var s = [2, 5, 10, 20, 30, 60, 120, 300, 600, 1800, 3600]; for (var i = 0; i < s.length; i++) if (v <= s[i]) return s[i]; return 3600; }
  function fmt(a) { return a >= 60 ? (Math.round(a / 6) / 10) + '°' : (Math.round(a * 10) / 10) + '′'; }
  function count(a) { return a < 0.5 ? 0 : a < 3 ? 1 : a < 12 ? 2 : 3; }
  function fullBright() { var b = BASE[colName] || BASE.red, m = Math.max(b[0], b[1], b[2]), f = 255 / m; return [Math.min(255, b[0] * f), Math.min(255, b[1] * f), Math.min(255, b[2] * f)]; }

  function draw() {
    var cv = $('cv'), g = cv.getContext('2d');
    var cssW = cv.clientWidth || 360, dpr = window.devicePixelRatio || 1;
    var px = Math.round(cssW * dpr);
    if (cv.width !== px) { cv.width = px; cv.height = px; cv.style.height = cssW + 'px'; }
    var S = px, sc = S / 440;             // design size 440
    g.setTransform(1, 0, 0, 1, 0, 0);
    g.fillStyle = '#000'; g.fillRect(0, 0, S, S);
    g.scale(sc, sc);
    var W = 440, cx = W / 2, cy = W / 2, rMax = W / 2 - 28;
    var red = rgb(dim), dimRed = rgb(dim * 0.55);
    var has = st && st.hasSolution, up = has ? st.moveUp : 0, east = has ? st.moveEast : 0;
    var q = st ? st.quality : 0;

    var err = has ? Math.sqrt(up * up + east * east) : 20;
    var range = niceCeil(Math.max(4, err * 1.3)), ppa = rMax / range;
    var r1 = ppa, r3 = ppa * 3;

    function circ(r, stroke, lw, fill, dash) {
      g.beginPath(); g.arc(cx, cy, r, 0, Math.PI * 2);
      if (fill) { g.fillStyle = fill; g.fill(); }
      g.setLineDash(dash || []); g.lineWidth = lw; g.strokeStyle = stroke; g.stroke(); g.setLineDash([]);
    }
    function label(t, x, y, size, color) { g.font = size + 'px sans-serif'; g.fillStyle = color; g.fillText(t, x, y + size); }

    for (var i = 1; i <= 3; i++) {
      var r = rMax * i / 3;
      if (Math.abs(r - r1) < 10 || Math.abs(r - r3) < 10) continue;
      circ(r, dimRed, 1.5);
      label(fmt(range * i / 3), cx + r * 0.71 + 4, cy - r * 0.71 - 12, 11, dimRed);
    }
    g.lineWidth = 1.5; g.strokeStyle = dimRed;
    g.beginPath(); g.moveTo(cx - rMax, cy); g.lineTo(cx + rMax, cy); g.moveTo(cx, cy - rMax); g.lineTo(cx, cy + rMax); g.stroke();

    var fb = fullBright(), glow = 'rgb(' + fb.map(Math.round).join(',') + ')', glowFill = 'rgba(' + fb.map(Math.round).join(',') + ',0.27)';
    var lit3 = q >= 1, lit1 = q >= 2;
    if (r3 <= rMax) {
      circ(r3, lit3 ? glow : dimRed, lit3 ? 4 : 2, lit3 && !lit1 ? glowFill : null, lit3 ? null : [6, 4]);
      if (r3 >= 14) label('3′', cx - r3 * 0.71 - 18, cy - r3 * 0.71 - 12, 11, lit3 ? glow : dimRed);
    }
    var rr = Math.max(r1, 3);
    circ(rr, lit1 ? glow : red, lit1 ? 5 : 2.5, lit1 ? glowFill : null);
    if (r1 >= 14) label('1′', cx + r1 * 0.71 + 4, cy + r1 * 0.71, 11, lit1 ? glow : red);
    g.beginPath(); g.arc(cx, cy, 3, 0, Math.PI * 2); g.fillStyle = red; g.fill();

    label('N', cx - 5, cy - rMax - 22, 13, red);
    label('E', cx + rMax + 6, cy - 9, 13, red);
    label('W', cx - rMax - 20, cy - 9, 13, red);

    if (!has) return;
    var dx = cx - east * ppa, dy = cy + up * ppa;
    var vx = dx - cx, vy = dy - cy, len = Math.sqrt(vx * vx + vy * vy);
    if (len > rMax) { dx = cx + vx / len * rMax; dy = cy + vy / len * rMax; }
    g.beginPath(); g.arc(dx, dy, 7, 0, Math.PI * 2); g.fillStyle = red; g.fill();
    g.setLineDash([5, 4]); g.lineWidth = 1.5; g.strokeStyle = red; g.beginPath(); g.moveTo(dx, dy); g.lineTo(cx, cy); g.stroke(); g.setLineDash([]);

    function chevrons(ux, uy, mag) {
      var n = count(mag);
      for (var k = 0; k < n; k++) {
        var d = rMax * 0.45 + 24 * k, tx = cx + ux * d, ty = cy + uy * d, s = 12, nx = -uy, ny = ux;
        g.beginPath(); g.moveTo(tx - ux * s + nx * s, ty - uy * s + ny * s); g.lineTo(tx, ty); g.lineTo(tx - ux * s - nx * s, ty - uy * s - ny * s);
        g.lineWidth = 4; g.lineCap = 'round'; g.lineJoin = 'round'; g.strokeStyle = red; g.stroke();
      }
    }
    chevrons(east > 0 ? 1 : -1, 0, Math.abs(east));
    chevrons(0, up > 0 ? -1 : 1, Math.abs(up));
  }
  window.addEventListener('resize', draw);

  // ---------- sound / wake lock ----------
  // The beep is a real <audio> element (a generated WAV): unlike Web Audio it is NOT muted by the iPhone silent switch.
  var beepEl = null, beepReady = false;
  function makeBeep() {
    try {
      var sr = 22050, n = Math.floor(sr * 0.14), buf = new ArrayBuffer(44 + n * 2), v = new DataView(buf);
      var tag = function (o, t) { for (var k = 0; k < t.length; k++) v.setUint8(o + k, t.charCodeAt(k)); };
      tag(0, 'RIFF'); v.setUint32(4, 36 + n * 2, true); tag(8, 'WAVE'); tag(12, 'fmt ');
      v.setUint32(16, 16, true); v.setUint16(20, 1, true); v.setUint16(22, 1, true);
      v.setUint32(24, sr, true); v.setUint32(28, sr * 2, true); v.setUint16(32, 2, true); v.setUint16(34, 16, true);
      tag(36, 'data'); v.setUint32(40, n * 2, true);
      for (var i = 0; i < n; i++) {
        var env = Math.min(1, i / 200, (n - i) / 400);
        v.setInt16(44 + i * 2, Math.sin(2 * Math.PI * 880 * i / sr) * env * 0.6 * 32767, true);
      }
      beepEl = new Audio(URL.createObjectURL(new Blob([buf], { type: 'audio/wav' })));
      beepEl.preload = 'auto';
    } catch (e) { beepEl = null; }
  }
  function webBeep() {
    try {
      if (!audio) audio = new (window.AudioContext || window.webkitAudioContext)();
      var o = audio.createOscillator(), gn = audio.createGain();
      o.frequency.value = 880; gn.gain.value = 0.15; o.connect(gn); gn.connect(audio.destination);
      o.start(); o.stop(audio.currentTime + 0.12);
    } catch (e) {}
  }
  function beep() {
    if (!beepEl) { webBeep(); return; }
    try {
      beepEl.muted = false; beepEl.currentTime = 0;
      var p = beepEl.play();
      if (p && p.catch) p.catch(function () { webBeep(); });
    } catch (e) { webBeep(); }
  }
  // Brighten the advice text for a moment each time Live view produces a new solution.
  var flashTimer = null;
  function flash() {
    var ids = ['altLine', 'altAdv', 'azLine', 'azAdv'];
    for (var i = 0; i < ids.length; i++) $(ids[i]).classList.add('flash');
    if (flashTimer) clearTimeout(flashTimer);
    flashTimer = setTimeout(function () {
      for (var j = 0; j < ids.length; j++) $(ids[j]).classList.remove('flash');
    }, 450);
  }
  function updToggles() {
    $('bSound').textContent = 'Phone beep: ' + (soundOn ? 'On' : 'Off') + (st && st.beep === false ? ' (off in plugin)' : '');
    var canWake = !!(navigator.wakeLock);
    $('bAwake').textContent = canWake ? 'Keep screen on: ' + (awakeOn ? 'On' : 'Off') : 'Screen stays on: set in phone settings';
    $('bAwake').disabled = !canWake;
    $('bSound').classList.toggle('on', soundOn); $('bAwake').classList.toggle('on', awakeOn);
  }
  function setWake() {
    try {
      if (awakeOn && navigator.wakeLock && document.visibilityState === 'visible') {
        navigator.wakeLock.request('screen').then(function (l) { wake = l; }).catch(function () {});
      } else if (!awakeOn && wake) { wake.release(); wake = null; }
    } catch (e) {}
  }
  document.addEventListener('visibilitychange', setWake);

  // iOS/Android only allow sound after a tap: create/resume the audio context on the first touch of the page.
  function unlockAudio() {
    try {
      if (!audio) audio = new (window.AudioContext || window.webkitAudioContext)();
      if (audio.state === 'suspended') audio.resume();
    } catch (e) {}
    try {
      if (beepEl && !beepReady) {
        beepReady = true;
        beepEl.muted = true;
        var p = beepEl.play();
        var done = function () { try { beepEl.pause(); beepEl.currentTime = 0; beepEl.muted = false; } catch (e) {} };
        if (p && p.then) p.then(done).catch(function () { beepEl.muted = false; beepReady = false; }); else done();
      }
    } catch (e) {}
  }
  makeBeep();
  document.addEventListener('touchstart', unlockAudio, { passive: true });
  document.addEventListener('click', unlockAudio);

  // ---------- wiring ----------
  function on(id, fn) { $(id).addEventListener('click', fn); }
  on('bAuto', function () { cmd('autocapture'); });
  on('bLive', function () { cmd('startlive'); });
  on('bStop', function () { cmd('stop'); });
  on('bReset', function () { cmd('reset'); });
  on('bAbort', function () { cmd('abortslew'); });
  on('confirmYes', function () { cmd('confirm'); });
  on('confirmNo', function () { cmd('cancelconfirm'); });
  on('bAltUnit', function () { cmd('altunit'); });
  on('bAzUnit', function () { cmd('azunit'); });
  on('bCal', function () { cmd('calibrate'); });
  on('calContinue', function () { cmd('calcontinue'); });
  on('calCancel', function () { cmd('calcancel'); });
  var crops = document.querySelectorAll('[data-crop]');
  for (var i = 0; i < crops.length; i++) (function (b) { b.addEventListener('click', function () { cmd('crop', b.getAttribute('data-crop')); }); })(crops[i]);
  var cols = document.querySelectorAll('[data-col]');
  for (var j = 0; j < cols.length; j++) (function (b) { b.addEventListener('click', function () { colName = b.getAttribute('data-col'); applyTheme(); }); })(cols[j]);
  on('bDimUp', function () { dim += 0.1; applyTheme(); });
  on('bDimDn', function () { dim -= 0.1; applyTheme(); });
  ['iExp:exp', 'iGain:gain', 'iBin:bin'].forEach(function (s) {
    var p = s.split(':'); $(p[0]).addEventListener('change', function () { cmd(p[1], $(p[0]).value); });
  });
  on('bSound', function () { soundOn = !soundOn; ls('pal.sound', soundOn ? '1' : '0'); updToggles(); if (soundOn) beep(); });
  on('bAwake', function () { awakeOn = !awakeOn; ls('pal.awake', awakeOn ? '1' : '0'); updToggles(); setWake(); });
  on('bSettings', function () { settingsOpen = !settingsOpen; ls('pal.settings', settingsOpen ? '1' : '0'); showSettings(); });
  function showSettings() { $('settings').classList.toggle('hidden', !settingsOpen); $('bSettings').innerHTML = 'Settings ' + (settingsOpen ? '&#9652;' : '&#9662;'); }
  on('pinOk', function () { pin = $('pinIn').value.trim(); ls('pal.pin', pin); $('pinMsg').textContent = 'Connecting...'; poll(); });

  showSettings(); updToggles(); applyTheme(); setWake(); poll();
})();
</script>
</body>
</html>
""";
    }
}
