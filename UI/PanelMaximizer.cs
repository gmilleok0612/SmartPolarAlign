using NINA.Core.Utility;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Media;

namespace PolarAlignLive.UI {

    /// <summary>
    /// "Maximize" for a NINA dock panel: hides every other tool panel in the same dock, gives this panel's
    /// pane (and its parents) nearly all of the dock space, and remembers everything so Restore puts it back.
    /// NINA uses Dirkster.AvalonDock 4.72 (assembly "AvalonDock"); it is driven by reflection so the plugin has
    /// no compile-time dependency on it. Everything is wrapped in try/catch: on any surprise it reports and does nothing.
    /// </summary>
    internal sealed class PanelMaximizer {

        private sealed class SavedSize { public object Element; public GridLength? Width; public GridLength? Height; }

        private readonly List<object> hidden = new List<object>();
        private readonly List<object> wasSelected = new List<object>();
        private readonly List<SavedSize> sizes = new List<SavedSize>();
        private object ours;

        public bool IsMaximized { get; private set; }

        // Titles of the panels hidden by Maximize are kept in a small file. NINA saves its dock layout when it exits, so
        // quitting while maximized would otherwise leave those panels hidden for good.
        private static string HiddenFile => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NINA", "PolarAlignLive.hiddenpanels");

        private bool exitHooked;

        private void HookExit() {
            if (exitHooked) return;
            try {
                var w = Application.Current?.MainWindow;
                if (w == null) return;
                w.Closing += (s, e) => { try { if (IsMaximized) Restore(); } catch { } };
                exitHooked = true;
            } catch { }
        }

        /// <summary>Re-shows panels that an earlier Maximize hid and never restored (for example NINA was closed while maximized).
        /// Returns -1 if the dock is not available yet, otherwise how many panels were shown.</summary>
        public int RecoverHidden(object content) {
            try {
                if (IsMaximized) return 0;
                if (!System.IO.File.Exists(HiddenFile)) return Find(content, out _, out _, out _) ? 0 : -1;
                if (!Find(content, out var mine, out var all, out _)) return -1;
                var titles = new HashSet<string>(System.IO.File.ReadAllLines(HiddenFile).Where(l => l.Length > 0));
                int n = 0;
                foreach (var a in all) {
                    if (ReferenceEquals(a, mine)) continue;
                    var t = Get(a, "Title") as string;
                    if (t == null || !titles.Contains(t)) continue;
                    if (Get(a, "IsVisible") is bool v && v) continue;
                    try { Call(a, "Show"); n++; } catch (Exception ex) { Logger.Info("PolarAlignLive recover Show failed: " + ex.Message); }
                }
                try { System.IO.File.Delete(HiddenFile); } catch { }
                Logger.Info("PolarAlignLive recovered " + n + " panel(s) hidden by an earlier Maximize.");
                return n;
            } catch (Exception ex) { Logger.Info("PolarAlignLive recover failed: " + ex.Message); return -1; }
        }

        public string Toggle(object content) {
            try {
                if (!IsMaximized && System.IO.File.Exists(HiddenFile)) {
                    int n = RecoverHidden(content);
                    if (n > 0) return $"Brought back {n} panel(s) that an earlier Maximize had hidden. Press Maximize again to maximize.";
                }
                return IsMaximized ? Restore() : Maximize(content);
            } catch (Exception ex) {
                Logger.Info("PolarAlignLive maximize failed: " + ex);
                return "Maximize failed: " + ex.Message;
            }
        }

        private static object Get(object o, string name) {
            var p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            return p?.GetValue(o);
        }

        private static bool Set(object o, string name, object value) {
            var p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p == null || !p.CanWrite) return false;
            p.SetValue(o, value);
            return true;
        }

        private static void Call(object o, string name) {
            o.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)?.Invoke(o, null);
        }

        private static IEnumerable<DependencyObject> Walk(DependencyObject root) {
            var stack = new Stack<DependencyObject>();
            stack.Push(root);
            while (stack.Count > 0) {
                var d = stack.Pop();
                yield return d;
                int n = (d is Visual || d is System.Windows.Media.Media3D.Visual3D) ? VisualTreeHelper.GetChildrenCount(d) : 0;
                for (int i = 0; i < n; i++) stack.Push(VisualTreeHelper.GetChild(d, i));
            }
        }

        private static IEnumerable<object> Descend(object root) {
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<object>();
            stack.Push(root);
            while (stack.Count > 0) {
                var e = stack.Pop();
                if (e == null || !seen.Add(e)) continue;
                yield return e;
                foreach (var name in new[] { "Children", "FloatingWindows", "RootPanel", "TopSide", "RightSide", "LeftSide", "BottomSide" }) {
                    object v;
                    try { v = Get(e, name); } catch { continue; }
                    if (v == null) continue;
                    if (v is IEnumerable en && !(v is string)) { foreach (var c in en) if (c != null) stack.Push(c); }
                    else if (name != "Children") stack.Push(v);
                }
            }
        }

        /// <summary>Finds the AvalonDock DockingManager whose layout holds an anchorable showing <paramref name="content"/>.</summary>
        private static bool Find(object content, out object anchorable, out List<object> allAnchorables, out string diag) {
            anchorable = null;
            allAnchorables = new List<object>();
            int managers = 0, seenAnchorables = 0;
            var titles = new List<string>();
            string myTitle = Get(content, "Title") as string;
            foreach (Window w in Application.Current.Windows) {
                foreach (var d in Walk(w)) {
                    if (d.GetType().FullName != "AvalonDock.DockingManager") continue;
                    managers++;
                    var layout = Get(d, "Layout");
                    if (layout == null) continue;
                    var all = Descend(layout).Where(e => e.GetType().Name == "LayoutAnchorable").ToList();
                    seenAnchorables += all.Count;
                    foreach (var a in all) titles.Add((Get(a, "Title") as string) ?? "?");
                    var mine = all.FirstOrDefault(a => ReferenceEquals(Get(a, "Content"), content))
                               ?? all.FirstOrDefault(a => myTitle != null && (Get(a, "Title") as string) == myTitle);
                    if (mine != null) { anchorable = mine; allAnchorables = all; diag = null; return true; }
                }
            }
            diag = $"dock managers: {managers}, panels seen: {seenAnchorables} [{string.Join(", ", titles.Take(12))}], looking for '{myTitle}'";
            return false;
        }

        /// <summary>True/false = whether NINA's dock currently shows this panel; null = panel not found in any dock layout.</summary>
        public bool? PanelVisible(object content) {
            try {
                if (!Find(content, out var mine, out _, out _)) return null;
                return Get(mine, "IsVisible") as bool?;
            } catch { return null; }
        }

        /// <summary>found, IsVisible, IsSelected of this panel's anchorable.</summary>
        public bool State(object content, out bool visible, out bool selected) {
            visible = false; selected = false;
            try {
                if (!Find(content, out var mine, out _, out _)) return false;
                visible = Get(mine, "IsVisible") as bool? ?? false;
                selected = Get(mine, "IsSelected") as bool? ?? false;
                return true;
            } catch { return false; }
        }

        /// <summary>Selects and activates this panel's tab (so one click on the top-bar icon brings it to the front).</summary>
        public void Activate(object content) {
            try {
                if (!Find(content, out var mine, out _, out _)) return;
                Set(mine, "IsSelected", true);
                Set(mine, "IsActive", true);
            } catch (Exception ex) { Logger.Info("PolarAlignLive activate failed: " + ex.Message); }
        }

        private string Maximize(object content) {
            if (!Find(content, out var mine, out var all, out var diag)) {
                Logger.Info("PolarAlignLive maximize: " + diag);
                return "Maximize: could not find this panel in NINA's dock. " + diag;
            }

            ours = mine;
            hidden.Clear(); wasSelected.Clear(); sizes.Clear();

            // 1) Hide every other visible tool panel, remembering which were the selected tab in their group.
            foreach (var a in all) {
                if (ReferenceEquals(a, mine)) continue;
                if (!(Get(a, "IsVisible") is bool vis) || !vis) continue;
                hidden.Add(a);
                if (Get(a, "IsSelected") is bool sel && sel) wasSelected.Add(a);
            }
            if (hidden.Count == 0) {
                return "Nothing to maximize: no other panels are open here. Open some from NINA's top bar first, then press Maximize.";
            }
            try {
                System.IO.File.WriteAllLines(HiddenFile, hidden.Select(a => Get(a, "Title") as string).Where(t => !string.IsNullOrEmpty(t)));
            } catch { }
            HookExit();
            foreach (var a in hidden) Call(a, "Hide");

            // 2) Give our pane and every parent most of the space (star sizes are relative to siblings).
            var big = new GridLength(1000, GridUnitType.Star);
            object p = mine;
            while (p != null) {
                var wProp = p.GetType().GetProperty("DockWidth");
                var hProp = p.GetType().GetProperty("DockHeight");
                if (wProp != null && hProp != null && wProp.CanWrite && hProp.CanWrite) {
                    var s = new SavedSize { Element = p, Width = (GridLength)wProp.GetValue(p), Height = (GridLength)hProp.GetValue(p) };
                    sizes.Add(s);
                    wProp.SetValue(p, big);
                    hProp.SetValue(p, big);
                }
                p = Get(p, "Parent");
            }

            Set(mine, "IsSelected", true);
            Set(mine, "IsActive", true);
            IsMaximized = true;
            return $"Maximized (hid {hidden.Count} other panel(s)). Press Restore to put them back.";
        }

        private string Restore() {
            // Show the hidden panels first (AvalonDock puts each back where it was), then restore sizes.
            foreach (var a in hidden) {
                try { Call(a, "Show"); } catch (Exception ex) { Logger.Info("PolarAlignLive restore Show failed: " + ex.Message); }
            }
            foreach (var s in sizes) {
                try {
                    if (s.Width.HasValue) Set(s.Element, "DockWidth", s.Width.Value);
                    if (s.Height.HasValue) Set(s.Element, "DockHeight", s.Height.Value);
                } catch (Exception ex) { Logger.Info("PolarAlignLive restore size failed: " + ex.Message); }
            }
            foreach (var a in wasSelected) { try { Set(a, "IsSelected", true); } catch { } }
            int n = hidden.Count;
            try { System.IO.File.Delete(HiddenFile); } catch { }
            hidden.Clear(); wasSelected.Clear(); sizes.Clear();
            IsMaximized = false;
            return $"Restored ({n} panel(s) shown again).";
        }
    }
}
