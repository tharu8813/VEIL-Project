// SPDX-License-Identifier: MPL-2.0
#pragma once
#include "protected_paths.h"
#include "broker_verification.h"

namespace veil_cef {
// This input MUST come from independent administrator-controlled registration,
// never from the child environment. Receipt v1 does not authenticate these
// fields. The product loader for that registration is not implemented yet.
struct RegisteredBootstrap {
  fs::path product_root;
  std::wstring volume_guid;
  std::string volume_id;
};

struct BootstrapEnvironment {
  Paths paths;
  fs::path temp, tmp;
};

enum class BootstrapCheck {
  kInvalidRegistration,
  kInvalidEnvironment,
  kInvalidPaths,
  kBrokerRejected,
  kChangedAfterReceipt,
  kPreflightVerified  // Not a product launch permit.
};

inline bool CheckEnvironment(const BootstrapEnvironment& supplied) {
  return DriveAbsolute(supplied.temp) && DriveAbsolute(supplied.tmp) &&
      Same(supplied.temp.native(), supplied.paths.scratch.native()) &&
      Same(supplied.tmp.native(), supplied.paths.scratch.native());
}

// Internal seam for a synthetic Windows regression. A receipt is requested
// exactly once, only after all local checks pass. Repeat the same local
// checks afterward; these snapshots do not solve TOCTOU or process lifetime.
template <typename Receipt, typename ReadEnvironment>
BootstrapCheck CheckBootstrap(const RegisteredBootstrap& registration,
                              Receipt receipt, ReadEnvironment read_environment) {
  std::string request;
  if (!DriveAbsolute(registration.product_root) ||
      !BuildRedemptionRequest(registration.volume_id, request) ||
      registration.volume_guid.empty())
    return BootstrapCheck::kInvalidRegistration;
  BootstrapEnvironment before;
  if (!read_environment(before) || !CheckEnvironment(before))
    return BootstrapCheck::kInvalidEnvironment;
  if (!VerifyPaths(registration.product_root, registration.volume_guid, before.paths))
    return BootstrapCheck::kInvalidPaths;
  if (!receipt(registration.volume_id))
    return BootstrapCheck::kBrokerRejected;
  BootstrapEnvironment after;
  if (!read_environment(after) || !CheckEnvironment(after) ||
      !VerifyPaths(registration.product_root, registration.volume_guid, after.paths) ||
      before.paths.root != after.paths.root || before.paths.cache != after.paths.cache ||
      before.paths.scratch != after.paths.scratch || before.paths.log != after.paths.log ||
      before.temp != after.temp || before.tmp != after.tmp)
    return BootstrapCheck::kChangedAfterReceipt;
  return BootstrapCheck::kPreflightVerified;
}

bool ReadBootstrapEnvironment(BootstrapEnvironment& supplied);
// Deliberately not called by RunWinMain/CefInitialize. Caller still needs
// independently authenticated registration, pinned loaded code, containment,
// live authority through shutdown and verified protected volume behavior.
BootstrapCheck VerifyRegisteredBootstrap(const RegisteredBootstrap& registration);
}  // namespace veil_cef
