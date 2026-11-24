# 35. Non-GET download compatibility (Stage 3-4a)

Debug-only Data URL prototype. Release remains blocked until engine and protected-volume tests. Cancel DownloadStarting first. No script injection, network replay, shell fallback, or application-created plaintext staging.

- One pending Data URL per regular session, 1 MiB decoded limit, strict ASCII/percent or standard Base64 subset, optional UTF-8/US-ASCII charset. Reject malformed/oversized data, unknown parameters, fragments and raw whitespace. Media type is not a safety guarantee.
- Require live active regular source tab, HTTPS or debug loopback HTTP, unchanged source/navigation epoch at deferred capture and after vault unlock. Apply source threat check, existing authenticated stream seal and file hash threat check; store source host only. Do not track data URLs as request methods. No retry or resume. Clear decoded buffer where possible.
- Chromium/managed immutable strings, cache, pagefile and dumps may retain data. Cancellation occurs after engine handling begins; no zero-write guarantee.
- POST stays blocked: replay may duplicate server-side effects and change authentication/CSRF context. Blob stays blocked: no arbitrary injected page JS or page-controlled message bridge. Service-worker-generated responses remain unsupported. Future support requires verified original response identity, bounded streaming, cancellation and protected disk routing.
- Synthetic policy smoke and existing data fixture length verification added. Actual WPF/VM and real sites NOT RUN. No release gate change.
