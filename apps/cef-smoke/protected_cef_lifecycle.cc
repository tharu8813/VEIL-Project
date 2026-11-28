// SPDX-License-Identifier: MPL-2.0
#include "protected_cef_lifecycle.h"

namespace veil_cef {
ProtectedLifecycleResult RunProtectedCefLifecycle(HINSTANCE instance,
    void* sandbox_info, ProtectedProcessRole role,
    const RegisteredBootstrap& registration,
    const std::function<bool()>& require_current,
    const std::function<ProtectedFixtureResult(const char*)>& run_fixture) {
  if (!instance || !sandbox_info || !require_current ||
      (role == ProtectedProcessRole::kBrowser && !run_fixture))
    return {ProtectedLifecycleStatus::kAuthorityRefused};
  CefMainArgs args(instance);
  std::unique_ptr<CefSettings> settings;
  // No CefApp callback can create a browser during initialization. The
  // future admitted fixture owns browser creation and close observations.
  return RunProtectedLifecycle(role, require_current,
      [&] { return CefExecuteProcess(args, nullptr, sandbox_info); },
      [&] {
        settings = PrepareProtectedInitializationSettings(registration);
        return settings != nullptr;
      },
      [&] { return CefInitialize(args, *settings, nullptr, sandbox_info); },
      [&] { return run_fixture(kProtectedSyntheticPage); },
      [] { CefShutdown(); });
}
}  // namespace veil_cef
