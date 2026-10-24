using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PolarAlignLive.Remote {

    public enum PairResult { Ok, Wrong, Expired, LockedOut }

    /// <summary>
    /// Phone access authentication. The NINA PC shows a random 6-digit pairing code (valid 15 minutes, regenerated every time
    /// phone access is switched on or "New pairing code" is pressed). Entering it on the phone returns a long random session
    /// token (192 bits); every later request must carry that token.
    /// Failed pairing attempts are throttled globally: after 3 wrong codes pairing is locked for 30 s, then 60 s, 120 s ... up to 15 min,
    /// so an attacker gets about 18 guesses at a 1-in-a-million code during its 15 minutes of life.
    /// A valid session token is always accepted, so an attacker cannot lock out a phone that is already paired.
    /// No NINA or WPF types here, so this is covered by the unit tests in tests/.
    /// </summary>
    public sealed class RemoteAuth {

        public const string Alphabet = "0123456789";
        public const int CodeLength = 6;
        public const int FailuresPerLock = 3;
        public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(15);
        public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(24);
        private const int MaxSessions = 8;
        private const int FirstLockSeconds = 30;
        private const int MaxLockSeconds = 900;

        private readonly Func<DateTime> clock;
        private readonly object sync = new object();
        private readonly Dictionary<string, DateTime> sessions = new Dictionary<string, DateTime>();
        private string code = "";
        private DateTime codeExpires = DateTime.MinValue;
        private int failures;
        private int lockLevel;
        private DateTime lockedUntil = DateTime.MinValue;

        public RemoteAuth() : this(null) { }
        public RemoteAuth(Func<DateTime> utcClock) { clock = utcClock ?? (() => DateTime.UtcNow); }

        /// <summary>The code as shown to the user (123-456), or "" when there is none or it has expired.</summary>
        public string DisplayCode {
            get {
                lock (sync) {
                    if (code.Length == 0 || clock() >= codeExpires) return "";
                    return code.Substring(0, 3) + "-" + code.Substring(3);
                }
            }
        }

        public bool HasValidCode => DisplayCode.Length > 0;

        /// <summary>Creates a fresh pairing code. Existing sessions are revoked and the throttle is reset.</summary>
        public string NewCode() {
            var sb = new StringBuilder(CodeLength);
            for (int i = 0; i < CodeLength; i++) sb.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
            lock (sync) {
                code = sb.ToString();
                codeExpires = clock() + CodeLifetime;
                sessions.Clear();
                failures = 0;
                lockLevel = 0;
                lockedUntil = DateTime.MinValue;
            }
            return DisplayCode;
        }

        /// <summary>Phone access switched off: forget the code and every session.</summary>
        public void Clear() {
            lock (sync) {
                code = "";
                codeExpires = DateTime.MinValue;
                sessions.Clear();
                failures = 0;
                lockLevel = 0;
                lockedUntil = DateTime.MinValue;
            }
        }

        /// <summary>Checks a code typed on the phone. On success returns a new session token.</summary>
        public PairResult TryPair(string given, out string sessionToken, out int retryAfterSeconds) {
            sessionToken = null;
            retryAfterSeconds = 0;
            lock (sync) {
                var t = clock();
                if (t < lockedUntil) {
                    retryAfterSeconds = (int)Math.Ceiling((lockedUntil - t).TotalSeconds);
                    return PairResult.LockedOut;
                }
                if (code.Length == 0 || t >= codeExpires) return PairResult.Expired;

                string norm = Normalize(given);
                bool ok = norm.Length == code.Length &&
                          CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(norm), Encoding.ASCII.GetBytes(code));
                if (!ok) {
                    failures++;
                    if (failures >= FailuresPerLock) {
                        failures = 0;
                        int secs = (int)Math.Min(MaxLockSeconds, FirstLockSeconds * Math.Pow(2, lockLevel));
                        if (lockLevel < 10) lockLevel++;
                        lockedUntil = t.AddSeconds(secs);
                        retryAfterSeconds = secs;
                        return PairResult.LockedOut;
                    }
                    return PairResult.Wrong;
                }

                failures = 0;
                lockLevel = 0;
                Prune(t);
                while (sessions.Count >= MaxSessions) {
                    var oldest = sessions.OrderBy(kv => kv.Value).First().Key;
                    sessions.Remove(oldest);
                }
                sessionToken = NewToken();
                sessions[sessionToken] = t + SessionLifetime;
                return PairResult.Ok;
            }
        }

        /// <summary>True for a token issued by TryPair that has not expired. Never throttled.</summary>
        public bool ValidateSession(string token) {
            if (string.IsNullOrEmpty(token)) return false;
            lock (sync) {
                var t = clock();
                Prune(t);
                return sessions.TryGetValue(token, out var exp) && t < exp;
            }
        }

        private void Prune(DateTime t) {
            var dead = sessions.Where(kv => t >= kv.Value).Select(kv => kv.Key).ToList();
            foreach (var k in dead) sessions.Remove(k);
        }

        private static string Normalize(string s) {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) if (c != '-' && !char.IsWhiteSpace(c)) sb.Append(char.ToUpperInvariant(c));
            return sb.ToString();
        }

        private static string NewToken() {
            byte[] b = RandomNumberGenerator.GetBytes(24);
            return Convert.ToBase64String(b).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }
    }
}
