// SPDX-License-Identifier: MPL-2.0
#include "protected_cef_lifecycle.h"
#include "protected_fixed_fixture.h"

namespace veil_cef {
ProtectedLifecycleResult RunProtectedCefLifecycle(HINSTANCE instance,
    void* sandbox_info, ProtectedProcessRole role,
    const RegisteredBootstrap& registration,
    const std::function<bool()>& require_current) {
  if (!instance || !sandbox_info || !require_current)
    return {ProtectedLifecycleStatus::kAuthorityRefused};
  CefMainArgs args(instance);
  std::unique_ptr<CefSettings> settings;
  // No CefApp callback can create a browser during initialization. The
  // fixed fixture owns browser creation and close observations.
  return RunProtectedLifecycle(role, require_current,
      [&] { return CefExecuteProcess(args, nullptr, sandbox_info); },
      [&] {
        settings = PrepareProtectedInitializationSettings(registration);
        return settings != nullptr;
      },
      [&] { return CefInitialize(args, *settings, nullptr, sandbox_info); },
      [&](const auto& current) { return RunProtectedFixedFixture(kProtectedSyntheticPage, current); },
      [] { CefShutdown(); });
}
}  // namespace veil_cef
