using System;
using System.Collections.Generic;

namespace PolarAlignLive.Astro {

    public readonly struct Vec3 {
        public readonly double X, Y, Z;
        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator -(Vec3 a) => new Vec3(-a.X, -a.Y, -a.Z);
        public static Vec3 operator *(Vec3 a, double s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        public static Vec3 operator *(double s, Vec3 a) => a * s;

        public double Dot(Vec3 o) => X * o.X + Y * o.Y + Z * o.Z;
        public Vec3 Cross(Vec3 o) => new Vec3(Y * o.Z - Z * o.Y, Z * o.X - X * o.Z, X * o.Y - Y * o.X);
        public double Length => System.Math.Sqrt(Dot(this));
        public Vec3 Normalized() { var l = Length; return l > 0 ? this * (1.0 / l) : this; }

        public static Vec3 FromRaDec(double raDeg, double decDeg) {
            double ra = raDeg * PolarMath.D2R, dec = decDeg * PolarMath.D2R;
            return new Vec3(System.Math.Cos(dec) * System.Math.Cos(ra), System.Math.Cos(dec) * System.Math.Sin(ra), System.Math.Sin(dec));
        }

        public void ToRaDec(out double raDeg, out double decDeg) {
            var v = Normalized();
            raDeg = System.Math.Atan2(v.Y, v.X) / PolarMath.D2R;
            if (raDeg < 0) raDeg += 360.0;
            decDeg = System.Math.Asin(System.Math.Max(-1.0, System.Math.Min(1.0, v.Z))) / PolarMath.D2R;
        }
    }

    /// <summary>One plate-solve result, J2000 degrees.</summary>
    public sealed class SolvedFrame {
        public double RaDeg { get; set; }
        public double DecDeg { get; set; }
        public double PositionAngleDeg { get; set; }
    }

    /// <summary>
    /// Orthonormal camera basis on the sky for a solved frame:
    /// B = boresight, U = image "up", R = image "right".
    /// paSign is the convention ambiguity of the solver's position angle (+1 or -1); it is auto-detected.
    /// Image mirroring does not matter: it flips R in every frame consistently and cancels out.
    /// </summary>
    public sealed class CameraFrame {
        public Vec3 R { get; }
        public Vec3 U { get; }
        public Vec3 B { get; }

        public CameraFrame(Vec3 r, Vec3 u, Vec3 b) { R = r; U = u; B = b; }

        public static CameraFrame Create(SolvedFrame f, int paSign) {
            var b = Vec3.FromRaDec(f.RaDeg, f.DecDeg);
            var z = new Vec3(0, 0, 1);
            var east = z.Cross(b).Normalized();     // local east at boresight
            var north = b.Cross(east);              // local north at boresight
            double p = paSign * f.PositionAngleDeg * PolarMath.D2R;
            var u = north * System.Math.Cos(p) + east * System.Math.Sin(p);
            var r = b.Cross(u);
            return new CameraFrame(r, u, b);
        }
    }

    public sealed class AxisSolution {
        /// <summary>RA axis expressed in camera components (x along R, y along U, z along B).</summary>
        public Vec3 CameraVector { get; set; }
        public int PaSign { get; set; }
        /// <summary>Consistency check of the third frame, degrees. Small (&lt; ~0.05) is good.</summary>
        public double ResidualDeg { get; set; }
        /// <summary>Residual of the rejected PA convention, for confidence reporting.</summary>
        public double RejectedResidualDeg { get; set; }
        public double RotationDeg { get; set; }
    }

    public static class PolarMath {
        public const double D2R = System.Math.PI / 180.0;
        public const double R2D = 180.0 / System.Math.PI;

        private const double Lever = 0.5; // radians-ish lever arm for the off-axis points

        public static double AngleDeg(Vec3 a, Vec3 b) {
            var d = a.Normalized().Dot(b.Normalized());
            d = System.Math.Max(-1.0, System.Math.Min(1.0, d));
            return System.Math.Acos(d) * R2D;
        }

        /// <summary>
        /// Direction of the rotation axis that carries frame f1 onto frame f2 (the mount RA axis),
        /// assuming the camera is rigid and only RA rotated. All difference vectors of fixed camera
        /// points are perpendicular to the axis, so the axis is the cross product of two of them.
        /// </summary>
        public static Vec3 AxisFromTwoFrames(CameraFrame f1, CameraFrame f2) {
            Vec3[] P(CameraFrame f) => new[] {
                f.B.Normalized(),
                (f.B + f.R * Lever).Normalized(),
                (f.B + f.U * Lever).Normalized()
            };
            var p1 = P(f1);
            var p2 = P(f2);
            var d = new Vec3[3];
            for (int i = 0; i < 3; i++) d[i] = p1[i] - p2[i];

            double bestLen = -1;
            Vec3 best = new Vec3(0, 0, 1);
            int[][] pairs = { new[] { 0, 1 }, new[] { 0, 2 }, new[] { 1, 2 } };
            foreach (var pr in pairs) {
                var c = d[pr[0]].Cross(d[pr[1]]);
                if (c.Length > bestLen) { bestLen = c.Length; best = c; }
            }
            var axis = best.Normalized();
            return axis.Z >= 0 ? axis : -axis; // northern-hemisphere pole side
        }

        /// <summary>Rotation angle (deg) between two frames about a given axis, measured on the boresights.</summary>
        public static double RotationAboutAxisDeg(Vec3 axis, CameraFrame f1, CameraFrame f2) {
            var a = axis.Normalized();
            var b1 = f1.B - a * a.Dot(f1.B);
            var b2 = f2.B - a * a.Dot(f2.B);
            if (b1.Length < 1e-9 || b2.Length < 1e-9) return 0;
            return AngleDeg(b1, b2);
        }

        /// <summary>
        /// Solve the RA axis from three frames taken at three RA positions (Dec unchanged).
        /// Frames 1 and 2 define the axis; frame 3 selects the correct position-angle sign convention
        /// by checking that the boresight stays equidistant from the axis.
        /// </summary>
        public static AxisSolution SolveAxis(IReadOnlyList<SolvedFrame> frames) {
            if (frames == null || frames.Count < 3) throw new ArgumentException("Three frames are required.");

            AxisSolution best = null;
            double other = double.NaN;
            foreach (int sign in new[] { 1, -1 }) {
                var f1 = CameraFrame.Create(frames[0], sign);
                var f2 = CameraFrame.Create(frames[1], sign);
                var f3 = CameraFrame.Create(frames[2], sign);
                var axis = AxisFromTwoFrames(f1, f2);

                double resid = System.Math.Abs(AngleDeg(axis, f3.B) - AngleDeg(axis, f1.B));
                // Average the camera-frame components seen from frames 1 and 2 to reduce noise.
                var c1 = new Vec3(axis.Dot(f1.R), axis.Dot(f1.U), axis.Dot(f1.B));
                var c2 = new Vec3(axis.Dot(f2.R), axis.Dot(f2.U), axis.Dot(f2.B));
                var c = ((c1 + c2) * 0.5).Normalized();

                var sol = new AxisSolution {
                    CameraVector = c,
                    PaSign = sign,
                    ResidualDeg = resid,
                    RotationDeg = RotationAboutAxisDeg(axis, f1, f2)
                };
                if (best == null || resid < best.ResidualDeg) {
                    if (best != null) other = best.ResidualDeg;
                    best = sol;
                } else {
                    other = resid;
                }
            }
            best.RejectedResidualDeg = other;
            return best;
        }

        /// <summary>Where the mount RA axis points on the sky (J2000) for a live solved frame.</summary>
        public static Vec3 AxisOnSky(AxisSolution sol, SolvedFrame live) {
            var f = CameraFrame.Create(live, sol.PaSign);
            var c = sol.CameraVector;
            return (f.R * c.X + f.U * c.Y + f.B * c.Z).Normalized();
        }

        // ---------- Local sky geometry ----------

        public static double LocalSiderealDeg(DateTime utc, double longitudeEastDeg) {
            double jd = 2451544.5 + (utc - new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalDays;
            double d = jd - 2451545.0;
            double gmst = 280.46061837 + 360.98564736629 * d;
            double lst = (gmst + longitudeEastDeg) % 360.0;
            return lst < 0 ? lst + 360.0 : lst;
        }

        /// <summary>Altitude and azimuth (azimuth from north through east), all degrees. RA/Dec of date.</summary>
        public static void AltAz(double raDeg, double decDeg, double latDeg, double lstDeg, out double altDeg, out double azDeg) {
            double h = (lstDeg - raDeg) * D2R;
            double dec = decDeg * D2R, lat = latDeg * D2R;
            double sinAlt = System.Math.Sin(dec) * System.Math.Sin(lat) + System.Math.Cos(dec) * System.Math.Cos(lat) * System.Math.Cos(h);
            altDeg = System.Math.Asin(System.Math.Max(-1.0, System.Math.Min(1.0, sinAlt))) * R2D;
            double y = -System.Math.Cos(dec) * System.Math.Sin(h);
            double x = System.Math.Sin(dec) * System.Math.Cos(lat) - System.Math.Cos(dec) * System.Math.Sin(lat) * System.Math.Cos(h);
            azDeg = System.Math.Atan2(y, x) * R2D;
            if (azDeg < 0) azDeg += 360.0;
        }

        /// <summary>
        /// Movement needed to bring the RA axis onto the north celestial pole, in arcminutes.
        /// moveUpArcmin &gt; 0: raise the axis (increase altitude). moveEastArcmin &gt; 0: swing the axis toward east.
        /// axisRaDeg/axisDecDeg must be of date (JNow).
        /// </summary>
        public static void PoleError(double axisRaDeg, double axisDecDeg, double latDeg, DateTime utc, double longitudeEastDeg,
                                     out double moveUpArcmin, out double moveEastArcmin, out double totalArcmin) {
            double lst = LocalSiderealDeg(utc, longitudeEastDeg);
            AltAz(axisRaDeg, axisDecDeg, latDeg, lst, out double alt, out double az);
            double azWrapped = az > 180.0 ? az - 360.0 : az; // pole is at az 0
            moveUpArcmin = (latDeg - alt) * 60.0;
            moveEastArcmin = -azWrapped * System.Math.Cos(alt * D2R) * 60.0;
            totalArcmin = (90.0 - axisDecDeg) * 60.0; // true polar distance of the axis
        }
    }
}
