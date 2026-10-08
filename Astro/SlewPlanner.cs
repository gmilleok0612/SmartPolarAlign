using System;

namespace PolarAlignLive.Astro {

    public sealed class SlewPlan {
        public double DecDeg;
        public double[] RaDeg = new double[2];   // targets for frames 2 and 3 (JNow)
        public int HaSign;                       // +1 west of meridian, -1 east
        public double StartRaDeg;                // where frame 1 is taken (current RA, or a safe repositioned RA)
        public bool NeedsReposition;             // true if the mount must first slew (RA only) to StartRaDeg
        public bool MovingAwayFromMeridian;
        public double MinAltSeen = 90, MaxAltSeen = -90;
    }

    /// <summary>
    /// Safety planner for the automatic RA-only slews. Hour angle HA = LST - RA (positive = west).
    /// At constant Dec, altitude is monotonic in |HA| while HA keeps its sign, so checking both ends of each
    /// move (now, and now + look-ahead for sidereal drift) is enough as long as the sign never flips.
    /// </summary>
    public static class SlewPlanner {

        public const double LookAheadMinutes = 20;
        public const double MeridianMarginDeg = 8;   // ~32 min of time

        public static double Wrap180(double d) {
            d %= 360.0;
            if (d > 180) d -= 360;
            if (d < -180) d += 360;
            return d;
        }

        public static double HourAngleDeg(double raDeg, double lonEastDeg, DateTime utc) =>
            Wrap180(PolarMath.LocalSiderealDeg(utc, lonEastDeg) - raDeg);

        public static bool Check(double raDeg, double decDeg, double latDeg, double lonEastDeg, DateTime utc,
                                 int haSign, double minAlt, double maxAlt, out string reason, out double lowAlt, out double highAlt) {
            lowAlt = 90; highAlt = -90;
            foreach (double dt in new[] { 0.0, LookAheadMinutes }) {
                var t = utc.AddMinutes(dt);
                double lst = PolarMath.LocalSiderealDeg(t, lonEastDeg);
                double ha = Wrap180(lst - raDeg);
                PolarMath.AltAz(raDeg, decDeg, latDeg, lst, out double alt, out double az);
                lowAlt = Math.Min(lowAlt, alt);
                highAlt = Math.Max(highAlt, alt);
                if (haSign != 0 && Math.Sign(ha) != haSign) { reason = "the move would cross the meridian"; return false; }
                if (Math.Abs(ha) < MeridianMarginDeg) { reason = $"too close to the meridian (within {MeridianMarginDeg / 15.0:0.0} h)"; return false; }
                if (alt > maxAlt) { reason = $"altitude {alt:0}° is above the {maxAlt:0}° zenith limit"; return false; }
                if (alt < minAlt) { reason = $"altitude {alt:0}° is below the {minAlt:0}° minimum"; return false; }
            }
            reason = null;
            return true;
        }

        /// <summary>
        /// Plan the capture positions. If the current position can't host the three frames safely, find the nearest
        /// RA (same Dec, same side of the meridian) that can, and plan a repositioning slew to it first.
        /// </summary>
        public static bool Plan(double ra0Deg, double decDeg, double latDeg, double lonEastDeg, DateTime utc,
                                double stepDeg, double minAlt, double maxAlt, out SlewPlan plan, out string reason) {
            plan = null;
            int haSign = Math.Sign(HourAngleDeg(ra0Deg, lonEastDeg, utc));
            if (haSign == 0) haSign = 1;

            if (PlanFrom(ra0Deg, decDeg, latDeg, lonEastDeg, utc, haSign, stepDeg, minAlt, maxAlt, out plan, out string r0)) {
                plan.StartRaDeg = ra0Deg; plan.NeedsReposition = false; reason = null; return true;
            }

            int away = haSign > 0 ? -1 : +1;
            for (int d = 1; d <= 179; d++) {
                foreach (int dir in new[] { away, -away }) {
                    double rs = ra0Deg + dir * d;
                    rs %= 360.0; if (rs < 0) rs += 360.0;
                    // The repositioning move itself must keep the same hour-angle sign.
                    if (!PlanFrom(rs, decDeg, latDeg, lonEastDeg, utc, haSign, stepDeg, minAlt, maxAlt, out var p, out _)) continue;
                    p.StartRaDeg = rs; p.NeedsReposition = true;
                    plan = p; reason = null;
                    return true;
                }
            }
            reason = "no safe RA at this Dec on this side of the meridian (" + r0 + "). Try a Dec nearer the celestial equator or lower the zenith limit";
            return false;
        }

        private static bool PlanFrom(double ra0Deg, double decDeg, double latDeg, double lonEastDeg, DateTime utc, int haSign,
                                     double stepDeg, double minAlt, double maxAlt, out SlewPlan plan, out string reason) {
            plan = null;
            if (!Check(ra0Deg, decDeg, latDeg, lonEastDeg, utc, haSign, minAlt, maxAlt, out reason, out double lo0, out double hi0)) {
                reason = "start position: " + reason;
                return false;
            }
            int away = haSign > 0 ? -1 : +1;
            string lastReason = null;
            foreach (int dir in new[] { away, -away }) {
                var p = new SlewPlan { DecDeg = decDeg, HaSign = haSign, MovingAwayFromMeridian = dir == away, MinAltSeen = lo0, MaxAltSeen = hi0 };
                bool ok = true;
                for (int k = 1; k <= 2 && ok; k++) {
                    double ra = ra0Deg + dir * k * stepDeg;
                    ra %= 360.0; if (ra < 0) ra += 360.0;
                    p.RaDeg[k - 1] = ra;
                    ok = Check(ra, decDeg, latDeg, lonEastDeg, utc, haSign, minAlt, maxAlt, out string r, out double lo, out double hi);
                    if (ok) { p.MinAltSeen = Math.Min(p.MinAltSeen, lo); p.MaxAltSeen = Math.Max(p.MaxAltSeen, hi); }
                    else lastReason = r;
                }
                if (ok) { plan = p; reason = null; return true; }
            }
            reason = lastReason ?? "no safe path";
            return false;
        }
    }
}
