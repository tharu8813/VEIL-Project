// SPDX-License-Identifier: MPL-2.0
#include "protected_initialization_settings.h"

namespace veil_cef {
std::unique_ptr<CefSettings> PrepareProtectedInitializationSettings(
    const RegisteredBootstrap& registration) {
  const auto plan = PrepareProtectedInitializationPlan(registration,
      ReadBootstrapEnvironment, [](const RegisteredBootstrap& registered,
                                    const Paths& paths) {
        return VerifyPaths(registered.product_root, registered.volume_guid, paths);
      });
  if (!plan) return nullptr;
  auto settings = std::make_unique<CefSettings>();
  settings->command_line_args_disabled = true;
  settings->no_sandbox = false;
  settings->remote_debugging_port = 0;
  settings->persist_session_cookies = false;
  CefString(&settings->root_cache_path) = plan->root_cache.wstring();
  CefString(&settings->cache_path) = plan->cache.wstring();
  CefString(&settings->log_file) = plan->log.wstring();
  // Scratch is the independently checked TEMP/TMP environment. It is
  // not a CEF cache fallback. These settings are not network isolation.
  return settings;
}
}  // namespace veil_cef
