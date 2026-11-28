# CEF authenticated system module baseline (P2-03)

Status: disconnected verifier and synthetic regression implementation; not a product admission mechanism.

The exact UTF-8 JSON payload is signed separately with RSA-PSS/SHA-256 using a 3072- or 4096-bit key. The schema is `veil-cef-system-modules-v1`, architecture is `x64`, and the document includes integer Windows build, updateRevision, sequence and a modules array. Each entry contains name, bytes, sha256 and signerSha256. The verifier refuses duplicate/unknown/missing fields, invalid module pins and case-insensitive duplicate names; the payload is limited to 1 MiB and 1024 modules.

The trusted public SPKI, expected host identity and minimum allowed sequence are independent inputs. Never accept a key from this document, derive the baseline from observed CEF modules, or trust the document's host/sequence as the comparison values. A valid signature does not establish OS provenance or the trustworthiness of the source manifest.

Remaining before integration:
- Independently obtain authenticated OS-specific file and signer pins from a reviewed release pipeline. No production signing key or baseline is created by this code.
- Establish protected trust-key installation, rotation/revocation and persistent minimum-sequence management; test rollback and update failures. The supplied minimum is not itself durable rollback protection.
- A read-only observer now checks native x64 OS/process architecture, RtlGetVersion build and 64-bit HKLM CurrentBuildNumber/UBR twice. VerifyCurrentHost rechecks after signature validation and before/after inventory. Real servicing races and administrative registry trust remain unverified; metadata cannot establish loaded-image provenance.
- Bind the resulting verifier to retained broker generation and runtime lease. Validate actual Windows catalog compatibility and protected CEF runtime in a disposable VM.
- Keep WebView2 product path and startupEnabled=false. Synthetic tests and compilation do not satisfy Gate A–E.
