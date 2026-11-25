# Development Windows policy observation (NOT RUN)

The exact pinned Chromium user_data_dir.cc source (PR #128) reads command-line path then HKLM/HKCU UserDataDir policy; HKLM takes precedence. The proposed, unregistered VEIL Chromium Dev product namespace implies a candidate SOFTWARE\\Policies\\VEIL Chromium Dev key. Do not change existing browser policies or assume this is the final registry root.

This standalone module only queries presence of UserDataDir in that development namespace, both hives and both WOW64 views. It does not retrieve the value contents, create keys, expand variables or alter policy. Any observed value (including an empty or unexpected type) is a blocker; access/other failures remain unknown, never treated as absent. Missing keys/values are absence observations only. All four queries must be classified before returning. Native and fake observer tests are pending Windows CI.

This is separate from PR #130 and is not yet connected to path validation or Chromium. No-value observation is NOT authorization, proof of absence over time, a trustworthy root, or an encrypted profile. Registry changes can race a later launch; native insertion, policy precedence, process ownership and protected volume identity need further review. Actual Chromium build/runtime/security gates NOT RUN. Do not register 0003 or launch a patched browser.
