using System;
using System.Linq;
using PolarAlignLive.Remote;
using Xunit;

namespace PolarAlignLive.Tests {

    public class RemoteAuthTests {

        private DateTime now = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
        private RemoteAuth Make() { return new RemoteAuth(() => now); }

        private static string Plain(string display) { return display.Replace("-", ""); }

        private static string WrongCode(string display) {
            string p = Plain(display);
            char first = p[0] == '1' ? '2' : '1';
            return first + p.Substring(1);
        }

        [Fact]
        public void NewCode_IsSixRandomDigits() {
            var a = Make();
            string c1 = a.NewCode();
            Assert.Equal(7, c1.Length);          // 123-456
            Assert.Equal('-', c1[3]);
            Assert.Equal(6, Plain(c1).Length);
            Assert.All(Plain(c1), ch => Assert.True(ch >= '0' && ch <= '9'));
            var seen = new System.Collections.Generic.HashSet<string>();
            for (int i = 0; i < 20; i++) seen.Add(a.NewCode());
            Assert.True(seen.Count > 15, "codes must be random, not repeated");
        }

        [Fact]
        public void NoCodeUntilGenerated_AndPairingThenReportsExpired() {
            var a = Make();
            Assert.Equal("", a.DisplayCode);
            Assert.Equal(PairResult.Expired, a.TryPair("123-456", out var token, out _));
            Assert.Null(token);
        }

        [Fact]
        public void CorrectCode_GivesALongSessionToken_AndOnlyThatTokenWorks() {
            var a = Make();
            string code = a.NewCode();
            Assert.Equal(PairResult.Ok, a.TryPair(code, out var token, out _));
            Assert.True(token.Length >= 32);
            Assert.True(a.ValidateSession(token));
            Assert.False(a.ValidateSession(token + "x"));
            Assert.False(a.ValidateSession(""));
            Assert.False(a.ValidateSession(null));
            Assert.False(a.ValidateSession(Plain(code)));    // the pairing code is not a session token
        }

        [Fact]
        public void CodeIsAcceptedInAnyCaseWithOrWithoutTheHyphenOrSpaces() {
            var a = Make();
            string code = a.NewCode();
            Assert.Equal(PairResult.Ok, a.TryPair(Plain(code).ToLowerInvariant(), out _, out _));
            Assert.Equal(PairResult.Ok, a.TryPair(" " + code.ToLowerInvariant() + " ", out _, out _));
        }

        [Fact]
        public void ThreeWrongCodes_LockPairing_EvenForTheCorrectCode() {
            var a = Make();
            string code = a.NewCode();
            for (int i = 0; i < 2; i++) Assert.Equal(PairResult.Wrong, a.TryPair(WrongCode(code), out _, out _));
            Assert.Equal(PairResult.LockedOut, a.TryPair(WrongCode(code), out _, out int retry));
            Assert.InRange(retry, 1, 30);
            Assert.Equal(PairResult.LockedOut, a.TryPair(code, out var token, out _));
            Assert.Null(token);
        }

        [Fact]
        public void LockoutEndsAfterItsTime_AndTheNextLockIsLonger() {
            var a = Make();
            string code = a.NewCode();
            for (int i = 0; i < 3; i++) a.TryPair(WrongCode(code), out _, out _);       // first lock: 30 s
            now = now.AddSeconds(31);
            for (int i = 0; i < 2; i++) Assert.Equal(PairResult.Wrong, a.TryPair(WrongCode(code), out _, out _));
            Assert.Equal(PairResult.LockedOut, a.TryPair(WrongCode(code), out _, out int retry)); // second lock: 60 s
            Assert.InRange(retry, 31, 60);
            now = now.AddSeconds(61);
            Assert.Equal(PairResult.Ok, a.TryPair(code, out _, out _));
        }

        [Fact]
        public void LockoutNeverExceedsFifteenMinutes() {
            var a = new RemoteAuth(() => now, TimeSpan.FromDays(30));   // the real code would expire during this long a run of lockouts
            string code = a.NewCode();
            int last = 0;
            for (int round = 0; round < 12; round++) {
                for (int i = 0; i < 3; i++) { a.TryPair(WrongCode(code), out _, out int r); if (r > 0) last = r; }
                now = now.AddSeconds(last + 1);
            }
            Assert.Equal(900, last);
        }

        [Fact]
        public void APairedPhoneKeepsWorkingDuringALockout() {
            var a = Make();
            string code = a.NewCode();
            Assert.Equal(PairResult.Ok, a.TryPair(code, out var token, out _));
            for (int i = 0; i < 3; i++) a.TryPair(WrongCode(code), out _, out _);
            Assert.True(a.ValidateSession(token));
        }

        [Fact]
        public void CodeExpiresAfterFifteenMinutes() {
            var a = Make();
            string code = a.NewCode();
            now = now.AddMinutes(14);
            Assert.Equal(PairResult.Ok, a.TryPair(code, out _, out _));
            now = now.AddMinutes(2);
            Assert.Equal(PairResult.Expired, a.TryPair(code, out _, out _));
            Assert.False(a.HasValidCode);
        }

        [Fact]
        public void NewCodeAndClear_RevokeEverySession() {
            var a = Make();
            a.TryPair(a.NewCode(), out var t1, out _);
            Assert.True(a.ValidateSession(t1));
            a.NewCode();
            Assert.False(a.ValidateSession(t1));
            a.TryPair(a.DisplayCode, out var t2, out _);
            Assert.True(a.ValidateSession(t2));
            a.Clear();
            Assert.False(a.ValidateSession(t2));
            Assert.Equal("", a.DisplayCode);
        }

        [Fact]
        public void SessionsExpireAfterTwentyFourHours_AndTheNumberOfSessionsIsCapped() {
            var a = Make();
            string code = a.NewCode();
            a.TryPair(code, out var first, out _);
            for (int i = 0; i < 10; i++) { now = now.AddSeconds(1); Assert.Equal(PairResult.Ok, a.TryPair(code, out _, out _)); }
            Assert.False(a.ValidateSession(first));    // oldest was dropped at the cap of 8
            a.TryPair(code, out var recent, out _);
            Assert.True(a.ValidateSession(recent));
            now = now.AddHours(25);
            Assert.False(a.ValidateSession(recent));
        }
    }
}
