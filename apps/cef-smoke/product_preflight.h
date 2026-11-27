// SPDX-License-Identifier: MPL-2.0
#pragma once
#include "bootstrap_registration.h"

namespace veil_cef {
// Every outcome is a denial of browser startup, including a successful
// preflight. These distinct exit codes contain no path or secret information.
enum class ProductPreflightExit : int {
  kVerifiedButDisabled = 29,
  kInvocationRejected = 30,
  kRegistrationRejected = 31,
  kBootstrapRejected = 32,
  kRegistrationChanged = 33,
  kUnexpectedFailure = 34,
};

template <typename Load, typename Verify>
ProductPreflightExit RunProductPreflight(bool invocation_allowed, Load load, Verify verify) {
  if (!invocation_allowed) return ProductPreflightExit::kInvocationRejected;
  try {
    RegisteredBootstrap before;
    if (!load(before)) return ProductPreflightExit::kRegistrationRejected;
    // Verify requests at most one broker receipt. Never retry after a lost
    // or rejected receipt, and never treat staging as redemption.
    if (verify(before) != BootstrapCheck::kPreflightVerified)
      return ProductPreflightExit::kBootstrapRejected;
    RegisteredBootstrap after;
    if (!load(after) ||
        before.product_root != after.product_root ||
        before.volume_guid != after.volume_guid ||
        before.volume_id != after.volume_id)
      return ProductPreflightExit::kRegistrationChanged;
    return ProductPreflightExit::kVerifiedButDisabled;
  } catch (...) {
    return ProductPreflightExit::kUnexpectedFailure;
  }
}
}  // namespace veil_cef
