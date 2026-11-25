# Chromium single-client cutover proposal (C4/C8/C9 preparation only)

PR #152 exact head f7081c7cdc79cd29c691bea9203ba97cb97caf24 [Windows CI](https://github.com/tharu8813/VEIL-Project/actions/runs/45674800568) PASS installer-shaped UTF8/BOM fixtures; merged dee880c62ec95c8b8413f765bf703dd309981786.

## Existing constraints

- BrokerConfig.cs b62b8ccd9e5f74a9bf6311f3e467a0ce3348033b: production permits exactly one ClientImages entry and a 64-hex ClientSha256; no fake backend/unsigned/development mode.
- Broker.cs e0612ffb2ba461a5ae5119e520d25dd66414c650 lines 122–131: nonzero session, registered SID, registered full image path, signature and hash required.
- scripts/broker-service.ps1 ee548f9218e4501ffa3814ebad7b4ea51f6215dc lines 167–185: current installer pins Veil.Desktop.exe, hash and existing volume/identity.
- scripts/package-maintenance.ps1 9747fcd15ff4f23454458b961d75860a3be91862 Set-BrokerHash only accepts the existing Desktop image. Do not use it to switch engines.

## Proposed administrator-reviewed cutover (not approved, not executable)

Preserve one active registered client, not a shared hash or broad allowlist. Do not add a second image, bypass signature, or impersonate legacy Desktop. Keep existing WebView2 source, binaries and recovery path. Synthetic target name in test is not a product installation path.

1. Require C1 actual upstream build, isolated C2 identifiers, C4 protection/session implementation, authenticated signed Chromium package and exact manifest hash, supported profile migration and separate protected profile directory. Verify legacy package and admin-only registration/volume identifiers. Obtain explicit administrator approval for a dedicated migration.
2. Verify all old/new browser and vault processes closed normally, volume detached and service stopped/disabled. No forced dismount, no simultaneous engines on the protected profile, no real secrets in scripts or receipts.
3. Preserve authenticated legacy package and byte-for-byte protected registration backup; verify backup and durable journal. Prepare a candidate changing ONLY clientImages (one exact new image) and clientSha256 (new signed executable hash). Preserve all other broker settings, volume/protector/user IDs and client configuration except separately reviewed profile migration. No arbitrary target from untrusted config.
4. Future implementation must validate target file identity and signed manifest, owner/ACL and path, use durable atomic replacement under admin control, verify resulting registration and journal before service activation; fail closed for unexpected changes or interrupted transaction.
5. Before activation commit, recovery may restore verified legacy state only if safe and detached. After commit or ambiguous activation, require administrator review/forward recovery; never auto-downgrade a potentially opened profile. Verify real service startup, unauthorized old/new client rejection as appropriate, Gate A–E, lock/detach and crash/power-loss recovery in isolated Windows VM before rollout.

## Implemented now

A pure C++17 candidate-only pin replacement contract checks current pin, different target image/hash and 12 synthetic prerequisites; 4096 boolean combinations, wrong pin, same identity, invalid hashes/paths and output preservation tests. No service/config/installer code changed, no JSON patch generated or registration written. Evidence booleans are not authenticated; path checks lexical only, hash strings not actual file hashes; no proof of signatures or runtime. Exact-head CI NOT RUN at commit. Actual signed package, safe migration, approval, real broker communication and Chromium build/runtime NOT RUN. Existing WebView2/Rust/broker and gates unchanged.
