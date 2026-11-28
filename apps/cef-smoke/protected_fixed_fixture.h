// SPDX-License-Identifier: MPL-2.0
#pragma once
#include "protected_lifecycle.h"
#include <functional>

namespace veil_cef {
// CEF main thread only, after successful initialization. No product caller.
// A false browsers_closed requires external broker termination, not shutdown.
ProtectedFixtureResult RunProtectedFixedFixture(const char* fixed_page,
    const std::function<bool()>& require_current);
}  // namespace veil_cef
