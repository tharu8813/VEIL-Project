// SPDX-License-Identifier: MPL-2.0
#pragma once
#include "protected_bootstrap.h"

namespace veil_cef {
// Dedicated, read-only registration format. The installer must separately
// create and reconcile this with the broker's admin configuration.
bool ParseBootstrapRegistration(const std::wstring& root,
                                const std::wstring& guid,
                                const std::wstring& id,
                                RegisteredBootstrap& result);
bool IsAdminOnlyRegistryDescriptor(PSECURITY_DESCRIPTOR descriptor);
bool LoadRegisteredBootstrap(RegisteredBootstrap& result);
}  // namespace veil_cef
