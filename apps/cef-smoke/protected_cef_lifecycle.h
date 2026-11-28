// SPDX-License-Identifier: MPL-2.0
#pragma once
#include "protected_initialization_settings.h"
#include "protected_lifecycle.h"
#include <functional>

namespace veil_cef {
// Disconnected CEF adapter. No production caller, IPC, browser admission
// or authority source yet. A future fixture must close every browser
// before reporting browsers_closed; the fixed URL is not network isolation.
ProtectedLifecycleResult RunProtectedCefLifecycle(HINSTANCE instance,
    void* sandbox_info, ProtectedProcessRole role,
    const RegisteredBootstrap& registration,
    const std::function<bool()>& require_current,
    const std::function<ProtectedFixtureResult(const char*)>& run_fixture);
}  // namespace veil_cef
