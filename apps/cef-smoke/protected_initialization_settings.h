// SPDX-License-Identifier: MPL-2.0
#pragma once
#include "include/cef_app.h"
#include "protected_initialization_plan.h"
#include <memory>

namespace veil_cef {
// Compiled settings adapter, deliberately disconnected from RunWinMain.
// No CEF execution or admission. Caller still needs a separate live
// authorization protocol and retained path/module/process checks.
std::unique_ptr<CefSettings> PrepareProtectedInitializationSettings(
    const RegisteredBootstrap& registration);
}  // namespace veil_cef
