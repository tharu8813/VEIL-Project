# 27. Password store maintenance (Stage 2-5)

- Authenticate existing manager before maintenance. Rotation authenticates old ciphertext in Rust, encrypts unchanged payload under the new passphrase, syncs ciphertext staging and atomically replaces the envelope. No plaintext staging. Existing backup passphrases do not change.
- Backup re-encrypts the authenticated list under a confirmed user-selected passphrase (12+ characters) and publishes a new user-selected file without overwriting. No CSV/plaintext export. A backup remains decryptable only with its own passphrase.
- Restore authenticates selected age ciphertext, validates format and account bounds, adds only missing exact origin/account pairs, asks for confirmation, then authenticates and replaces the existing store with the current manager password. Existing entries win conflicts. Invalid, oversized or tampered backup fails; cancellation leaves entries unchanged.
- To restore on a new profile, first initialize its manager password. A forgotten existing manager password is not bypassed; recovery into a fresh profile requires a valid backup and its passphrase. Never reset a corrupt existing store silently.
- Passwords travel via bounded process stdin, never command arguments; secret buffers cleared where possible. No protection from an already compromised unlocked process. Atomic replacement does not guarantee secure deletion of old disk blocks or old backups.
- Added Rust rotation and CLI tests and merge smoke assertion. CI pending; actual Windows UI, failure/power interruption and protected-volume verification NOT RUN. No release gate change.

## Maintenance UI regression

Added `p6-password-maintenance` and `p6-password-maintenance-read` to the Debug Phase 6 batch: backup cancellation, mismatch, collision, wrong backup password, tampering, cancelled restore, missing-account merge, duplicate-only restore, short/mismatched replacement, rotation and restart. The synthetic credential scan also covers the new fixtures. File picker/confirmation/secret hooks are Debug-only; Release still requires the real prompts. Existing master authentication and no-recovery policy are unchanged. Actual interactive Windows and protected-volume execution: **NOT RUN**. Download vault content-key UI is a separate follow-up.
