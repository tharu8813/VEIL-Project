// SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;

namespace Veil.Desktop;

// Disabled candidate only: one nomination, one resume, one bounded exit
// observation. No successful browser startup is reported by this method.
internal static class CefDisabledPreflight
{
    internal const uint VerifiedButDisabled = 29;

    internal static uint Run(CefContainedProcess child, Action stage, Func<bool> stillAuthorized)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(stillAuthorized);
        // The broker's stage lifetime is 15 seconds, counted by the broker.
        // This more conservative local deadline includes the staging request.
        var elapsed = Stopwatch.StartNew();
        bool Current() => elapsed.Elapsed < TimeSpan.FromSeconds(12) && stillAuthorized() &&
            elapsed.Elapsed < TimeSpan.FromSeconds(12);
        try
        {
            if (!Current()) throw new InvalidOperationException("CEF preflight preparation expired.");
            stage();
            child.ResumeAfterVerification(Current);
            int remaining = Math.Max(0, 12000 - (int)Math.Min(12000, elapsed.ElapsedMilliseconds));
            if (!child.WaitForExit(remaining))
                throw new TimeoutException("Disabled CEF candidate did not exit in time.");
            uint code = child.ReadExitCode();
            if (code != VerifiedButDisabled)
                throw new InvalidOperationException("CEF disabled preflight did not report verification.");
            if (!Current()) throw new InvalidOperationException("CEF preparation changed during preflight.");
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
