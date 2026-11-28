// SPDX-License-Identifier: MPL-2.0
#pragma once
#include "protected_initialization_settings.h"
#include "protected_lifecycle.h"
#include <functional>

namespace veil_cef {
// Disconnected CEF adapter. No production caller or admission authority.
// Only the fixed fixture is allowed; the fixed URL is not network isolation.
ProtectedLifecycleResult RunProtectedCefLifecycle(HINSTANCE instance,
    void* sandbox_info, ProtectedProcessRole role,
    const RegisteredBootstrap& registration,
    const std::function<bool()>& require_current);
}  // namespace veil_cef
