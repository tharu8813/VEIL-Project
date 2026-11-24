# 26. Site login actions (Stage 2-4)

- The selected HTTPS page is probed after successful navigation or tab selection. Probe returns a boolean only, not field values, and shows a native login-action icon for a supported visible form. Dynamic forms can use the main menu. No injected submit observer, WebMessage, host objects, engine password autosave or general autofill.
- Explicit save/update command: authenticate the existing separate age store (or set/confirm its password), read the supported current form only, confirm native origin and save/update, then create/replace authenticated storage. Cancel or unsupported page leaves credentials unchanged.
- Explicit fill command: authenticate, choose an account for the exact origin, verify tab/owner/epoch/active window and form again, fill fields without submission or replacing a nonempty password. Manual manager remains.
- Shared selector rejects frames, non-HTTPS origin, multiple password fields, new-password/OTP fields, ambiguous usernames and cross-origin form actions/overrides. Closing tab invalidates permission/credential context.
- Browser content already controls its own form; this is not protection from a malicious same-origin page or memory inspection. Managed strings and WebView script execution can contain secrets temporarily; do not log them. No automatic capture after login submission or silent fill.
- Synthetic policy smoke added. Actual HTTPS form tests, account picker/setup flow, page changes, SPA compatibility and installed protected profile tests NOT RUN. No release gate changes.
