// SPDX-License-Identifier: MPL-2.0
#pragma once
#include "protected_bootstrap.h"
#include <optional>

namespace veil_cef {
// A configuration snapshot ONLY. It confers no launch permission, and
// does not replace retained profile, broker generation or runtime leases.
struct ProtectedInitializationPlan {
  fs::path root_cache, cache, scratch, log;
};

inline constexpr char kProtectedSyntheticPage[] =
    "data:text/html,%3Ctitle%3EVEIL_CEF_PROTECTED_TEST%3C/title%3E";

inline bool MatchesProtectedLayout(const RegisteredBootstrap& registration,
                                   const BootstrapEnvironment& environment) {
  const auto root = registration.product_root / L"cef";
  const auto& paths = environment.paths;
  std::string request;
  if (!DriveAbsolute(registration.product_root) || registration.volume_guid.empty() ||
      !BuildRedemptionRequest(registration.volume_id, request) ||
      !CheckEnvironment(environment)) return false;
  for (const auto& path : {paths.root, paths.cache, paths.scratch, paths.log})
    if (!DriveAbsolute(path)) return false;
  return Same(paths.root.native(), root.native()) &&
      Same(paths.cache.native(), (root / L"cache").native()) &&
      Same(paths.scratch.native(), (root / L"temp").native()) &&
      Same(paths.log.native(), (root / L"logs" / L"cef.log").native());
}

// Independent registration is supplied by the future admission caller.
// Re-read the environment and filesystem before returning a plan. Never
// create paths, inherit an alternate profile, or use a temp fallback.
template <typename ReadEnvironment, typename InspectPaths>
std::optional<ProtectedInitializationPlan> PrepareProtectedInitializationPlan(
    const RegisteredBootstrap& registration, ReadEnvironment read_environment,
    InspectPaths inspect_paths) {
  try {
    BootstrapEnvironment before, after;
    if (!read_environment(before) || !MatchesProtectedLayout(registration, before) ||
        !inspect_paths(registration, before.paths) ||
        !read_environment(after) || !MatchesProtectedLayout(registration, after) ||
        before.paths.root != after.paths.root || before.paths.cache != after.paths.cache ||
        before.paths.scratch != after.paths.scratch || before.paths.log != after.paths.log ||
        before.temp != after.temp || before.tmp != after.tmp ||
        !inspect_paths(registration, after.paths)) return std::nullopt;
    return ProtectedInitializationPlan{after.paths.root, after.paths.cache,
        after.paths.scratch, after.paths.log};
  } catch (...) {
    return std::nullopt;
  }
}
}  // namespace veil_cef
