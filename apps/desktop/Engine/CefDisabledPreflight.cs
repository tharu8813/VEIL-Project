// SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;

namespace Veil.Desktop;

// Disabled candidate only: one nomination, one resume, one bounded exit
// observation. No successful browser startup is reported by this method.
internal static class CefDisabledPreflight
{
    internal const uint VerifiedButDisabled = 29;

    internal static uint Run(CefContainedProcess child, Action stage, Func<bool> stillAuthorized, Action confirm)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(stillAuthorized);
        ArgumentNullException.ThrowIfNull(confirm);
        // The broker's stage lifetime is 15 seconds, counted by the broker.
        // Full runtime hashing happens during staging, before the broker issues
        // the admission. Start the local redemption budget after its reply.
        var elapsed = new Stopwatch();
        bool Current() => elapsed.Elapsed < TimeSpan.FromSeconds(12) && stillAuthorized() &&
            elapsed.Elapsed < TimeSpan.FromSeconds(12);
        try
        {
            if (!stillAuthorized()) throw new InvalidOperationException("CEF preflight preparation expired.");
            stage();
            elapsed.Start();
            child.ResumeAfterVerification(Current);
            // Do not block for the entire remaining budget without checking
            // authority. Poll on the owning thread, with bounded wait slices.
            // Current may itself block in broker/volume checks; this is not
            // a real-time watchdog or a dispatcher message pump.
            while (!child.WaitForExit(0))
            {
                if (!Current()) throw new InvalidOperationException("CEF preflight authority expired or was revoked.");
                if (!child.ImageLoadMitigationsActive)
                    throw new InvalidOperationException("CEF image loading policy changed.");
                int remaining = Math.Max(0, 12000 - (int)Math.Min(12000, elapsed.ElapsedMilliseconds));
                if (remaining == 0) throw new TimeoutException("Disabled CEF candidate did not exit in time.");
                if (child.WaitForExit(Math.Min(250, remaining))) break;
            }
            uint code = child.ReadExitCode();
            if (code != VerifiedButDisabled)
                throw new InvalidOperationException("CEF disabled preflight did not report verification.");
            if (!Current()) throw new InvalidOperationException("CEF preparation changed during preflight.");
            // Never manufacture a successful exit by killing the job.
            // The candidate is not expected to leave any subprocess alive.
            if (!child.WaitForNaturalJobExit(250) || !Current())
                throw new InvalidOperationException("CEF candidate left a live job member or lost authority.");
            confirm();
            if (!Current()) throw new InvalidOperationException("CEF confirmation expired.");
            return code;
        }
        finally
        {
            // Stage or resume failures may already have closed the job.
            // Otherwise confirm all members have exited before releasing pins.
            try { child.TerminateAndDrain(5000); }
            catch (ObjectDisposedException) { } // Kill-on-close has already been requested, not drain proof.
        }
    }
}
