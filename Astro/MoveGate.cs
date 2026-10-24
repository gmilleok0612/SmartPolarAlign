using System;
using System.Threading;
using System.Threading.Tasks;

namespace PolarAlignLive.Astro {

    public enum MoveOutcome { Done, Stopped, TrackingFailed, SlewIncomplete }

    /// <summary>
    /// All Stop gate for one Find Axis run. Every movement of the run (the tracking-start wait, the optional reposition
    /// slew, the two capture slews and the settle waits) goes through this gate, so once Stop() has been called NO further
    /// movement can start, whatever the run was doing at that moment. Exposures and plate solves are deliberately not
    /// routed through it: they are allowed to finish.
    /// No NINA or WPF types here, so the gate is covered by the unit tests in tests/.
    /// </summary>
    public sealed class MoveGate : IDisposable {

        private readonly CancellationTokenSource stopCts = new CancellationTokenSource();
        private readonly CancellationTokenSource linked;
        private int stopped;

        /// <param name="runToken">The run's own cancellation token (the Stop button); it also cancels the gate's token.</param>
        public MoveGate(CancellationToken runToken) {
            linked = CancellationTokenSource.CreateLinkedTokenSource(runToken, stopCts.Token);
        }

        public bool IsStopped => Volatile.Read(ref stopped) != 0;

        /// <summary>Cancelled by All Stop or by the run's own token. Pass it to every slew and wait.</summary>
        public CancellationToken Token => linked.Token;

        public void Stop() {
            Interlocked.Exchange(ref stopped, 1);
            try { stopCts.Cancel(); } catch (ObjectDisposedException) { }
        }

        /// <summary>A wait that ends immediately (with OperationCanceledException) when All Stop is pressed.</summary>
        public Task Delay(int milliseconds) {
            return Task.Delay(milliseconds, linked.Token);
        }

        /// <summary>
        /// Runs one slew only if All Stop has not been pressed. If All Stop arrives before, during or just after the slew,
        /// the mount is stopped again and Stopped is returned, so the caller never goes on to another movement.
        /// </summary>
        public async Task<MoveOutcome> SlewAsync(Func<CancellationToken, Task<bool>> slew, Action stopMount) {
            if (IsStopped) return MoveOutcome.Stopped;
            bool done;
            try {
                done = await slew(linked.Token);
            } catch (OperationCanceledException) when (IsStopped) {
                SafeStop(stopMount);
                return MoveOutcome.Stopped;
            }
            if (IsStopped) {
                SafeStop(stopMount);
                return MoveOutcome.Stopped;
            }
            return done ? MoveOutcome.Done : MoveOutcome.SlewIncomplete;
        }

        /// <summary>
        /// Switches tracking on and waits for the mount to settle. All Stop pressed during the wait ends it at once, and the
        /// caller gets Stopped, so the reposition slew that follows is never started.
        /// </summary>
        public async Task<MoveOutcome> StartTrackingAsync(Func<bool> setTrackingOn, Func<bool> isTrackingOn, int delayMilliseconds) {
            if (IsStopped) return MoveOutcome.Stopped;
            bool on = setTrackingOn();
            try {
                await Delay(delayMilliseconds);
            } catch (OperationCanceledException) when (IsStopped) {
                return MoveOutcome.Stopped;
            }
            if (IsStopped) return MoveOutcome.Stopped;
            return on && isTrackingOn() ? MoveOutcome.Done : MoveOutcome.TrackingFailed;
        }

        private static void SafeStop(Action stopMount) {
            try { stopMount?.Invoke(); } catch { }
        }

        public void Dispose() {
            linked.Dispose();
            stopCts.Dispose();
        }
    }
}
