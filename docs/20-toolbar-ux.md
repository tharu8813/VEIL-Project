# Toolbar UX: download progress and bookmarks

- The download toolbar button remains visible and toggles a native recent-download popup without changing tabs. The native list shows active transfers/cancel, recent authenticated vault entries (only while unlocked), and recent non-stored outcomes from this session. No file is opened or exported from the popup; existing vault and full download controls are retained.
- The ring shows byte-weighted aggregate progress for known lengths; queued/unknown-length and final authentication are indeterminate. A short UI timer runs only while transfers are active or the popup is open. No network or storage path is changed.
- Bookmark bar under the address toolbar is visible by default, can be toggled using Ctrl+Shift+B or the menu, and is saved in the existing protected browser state. Existing bookmarks are local only; no favicon lookups. Clicking navigates through existing address handling; Ctrl+click opens a new tab. Changes and visibility sync across profile windows. Full screen hides the bar.
- Added smoke assertions for progress aggregation, unknown sizes, popup opening without navigation, full download page, bookmark bar creation/toggle/persistence. Interactive Windows/VM tests and visual/DPI checks remain required. No security release gate is changed.

## Stage 1 stabilization: shared window state

- Refresh currently opened bookmark libraries after another window commits an edit, preserving a still-valid selection and rejecting stale selected entries. Visibility-only changes do not rebuild library rows.
- Refresh download history pages across all live windows (including background tabs), rather than just the initiating active tab. Ignore disposed/closing views. A queued update is not lost merely because the originating window closes.
- Stop the vault unlock/setup retry path after cancellation, shutdown or owner-window closure; never display a new prompt after that point. Recovery without completed credential reset still relocks in the existing finally block. An already committed setup/credential reset is not rolled back.
- Extend existing multiwindow smoke to verify bookmark bar visibility and persistence in both windows. Actual Windows/VM smoke, library-edit synchronization and unlock cancellation during native prompts remain NOT RUN; no release gate changes.
