using System;
using System.Threading;
using System.Threading.Tasks;
using PolarAlignLive.Astro;
using Xunit;

namespace PolarAlignLive.Tests {

    public class MoveGateTests {

        /// <summary>
        /// Regression test for the review finding: tracking was off, Find Axis needs a reposition slew, and All Stop is pressed
        /// during the 1.5 s tracking-start wait. The reposition slew must never start (and nothing may move afterwards).
        /// This follows the same sequence AutoCaptureCore runs: StartTrackingAsync, then SlewAsync only if tracking returned Done.
        /// </summary>
        [Fact]
        public async Task AllStopDuringTrackingStart_NeverStartsTheRepositionSlew() {
            using (var gate = new MoveGate(CancellationToken.None)) {
                int slewsStarted = 0, trackingOnCalls = 0;

                var tracking = gate.StartTrackingAsync(() => { trackingOnCalls++; return true; }, () => true, 1500);
                await Task.Delay(100);          // the operator presses All Stop inside the 1.5 s wait
                gate.Stop();

                var started = DateTime.UtcNow;
                var trackingResult = await tracking;
                Assert.True(DateTime.UtcNow - started < TimeSpan.FromMilliseconds(1000), "the tracking wait must end at once on All Stop");
                Assert.Equal(1, trackingOnCalls);
                Assert.Equal(MoveOutcome.Stopped, trackingResult);

                // The caller stops on Stopped. Even a caller that did not check must still be refused by the gate.
                var reposition = await gate.SlewAsync(tok => { slewsStarted++; return Task.FromResult(true); }, () => { });
                Assert.Equal(MoveOutcome.Stopped, reposition);
                Assert.Equal(0, slewsStarted);
            }
        }

        [Fact]
        public async Task AllStopBeforeTrackingStart_DoesNotEvenSwitchTrackingOn() {
            using (var gate = new MoveGate(CancellationToken.None)) {
                gate.Stop();
                int calls = 0;
                var r = await gate.StartTrackingAsync(() => { calls++; return true; }, () => true, 1500);
                Assert.Equal(MoveOutcome.Stopped, r);
                Assert.Equal(0, calls);
            }
        }

        [Fact]
        public async Task TrackingStart_CompletesNormallyWhenNotStopped() {
            using (var gate = new MoveGate(CancellationToken.None)) {
                var r = await gate.StartTrackingAsync(() => true, () => true, 10);
                Assert.Equal(MoveOutcome.Done, r);
                var bad = await gate.StartTrackingAsync(() => true, () => false, 10);
                Assert.Equal(MoveOutcome.TrackingFailed, bad);
            }
        }

        [Fact]
        public async Task AllStopBeforeSlew_SlewIsNotStarted() {
            using (var gate = new MoveGate(CancellationToken.None)) {
                gate.Stop();
                int started = 0;
                var r = await gate.SlewAsync(tok => { started++; return Task.FromResult(true); }, () => { });
                Assert.Equal(MoveOutcome.Stopped, r);
                Assert.Equal(0, started);
            }
        }

        [Fact]
        public async Task AllStopDuringSlew_CancelsTheSlewAndStopsTheMount() {
            using (var gate = new MoveGate(CancellationToken.None)) {
                int stopCalls = 0;
                var running = gate.SlewAsync(async tok => { await Task.Delay(Timeout.Infinite, tok); return true; }, () => stopCalls++);
                await Task.Delay(100);
                gate.Stop();
                var r = await running;
                Assert.Equal(MoveOutcome.Stopped, r);
                Assert.True(stopCalls >= 1);
            }
        }

        [Fact]
        public async Task AllStopRacingWithSlewCompletion_StillReportsStopped() {
            using (var gate = new MoveGate(CancellationToken.None)) {
                int stopCalls = 0;
                var r = await gate.SlewAsync(tok => { gate.Stop(); return Task.FromResult(true); }, () => stopCalls++);
                Assert.Equal(MoveOutcome.Stopped, r);
                Assert.Equal(1, stopCalls);
            }
        }

        [Fact]
        public async Task AllStopDuringSettleWait_EndsTheWait() {
            using (var gate = new MoveGate(CancellationToken.None)) {
                var wait = gate.Delay(5000);
                gate.Stop();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.Delay(5000));
            }
        }

        [Fact]
        public async Task UserStopButton_IsNotReportedAsAllStop() {
            using (var cts = new CancellationTokenSource())
            using (var gate = new MoveGate(cts.Token)) {
                var running = gate.SlewAsync(async tok => { await Task.Delay(Timeout.Infinite, tok); return true; }, () => { });
                await Task.Delay(50);
                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
                Assert.False(gate.IsStopped);
            }
        }

        [Fact]
        public async Task NormalSlew_ReportsDoneOrIncomplete() {
            using (var gate = new MoveGate(CancellationToken.None)) {
                Assert.Equal(MoveOutcome.Done, await gate.SlewAsync(tok => Task.FromResult(true), () => { }));
                Assert.Equal(MoveOutcome.SlewIncomplete, await gate.SlewAsync(tok => Task.FromResult(false), () => { }));
            }
        }
    }
}
