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

        public string Toggle(object content) {
            try {
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

        /// <summary>Finds the AvalonDock DockingManager whose layout holds an anchorable showing <paramref name="content"/>.</summary>
        private static bool Find(object content, out object anchorable, out List<object> allAnchorables) {
            anchorable = null;
            allAnchorables = new List<object>();
            foreach (Window w in Application.Current.Windows) {
                foreach (var d in Walk(w)) {
                    if (d.GetType().FullName != "AvalonDock.DockingManager") continue;
                    var layout = Get(d, "Layout");
                    if (layout == null) continue;
                    var all = new List<object>();
                    var m = layout.GetType().GetMethod("Descendents", BindingFlags.Public | BindingFlags.Instance);
                    if (m == null) continue;
                    foreach (var e in (IEnumerable)m.Invoke(layout, null)) {
                        if (e.GetType().Name == "LayoutAnchorable") all.Add(e);
                    }
                    var mine = all.FirstOrDefault(a => ReferenceEquals(Get(a, "Content"), content));
                    if (mine != null) { anchorable = mine; allAnchorables = all; return true; }
                }
            }
            return false;
        }

        private string Maximize(object content) {
            if (!Find(content, out var mine, out var all))
                return "Maximize: could not find this panel in NINA's dock (is it docked/visible?).";

            ours = mine;
            hidden.Clear(); wasSelected.Clear(); sizes.Clear();

            // 1) Hide every other visible tool panel, remembering which were the selected tab in their group.
            foreach (var a in all) {
                if (ReferenceEquals(a, mine)) continue;
                if (!(Get(a, "IsVisible") is bool vis) || !vis) continue;
                hidden.Add(a);
                if (Get(a, "IsSelected") is bool sel && sel) wasSelected.Add(a);
            }
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
            hidden.Clear(); wasSelected.Clear(); sizes.Clear();
            IsMaximized = false;
            return $"Restored ({n} panel(s) shown again).";
        }
    }
}
