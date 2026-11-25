// SPDX-License-Identifier: MPL-2.0
// Standalone synthetic development observation. Never authorizes launch.
#ifndef VEIL_CHROMIUM_DEV_PROFILE_COMBINED_WIN_H_
#define VEIL_CHROMIUM_DEV_PROFILE_COMBINED_WIN_H_
#include "profile-filesystem-win.h"
#include "profile-policy-win.h"

namespace veil::chromium_dev {
enum class CombinedObservation {
  kLexicalRejected,
  kPolicyOverrideObserved,
  kPolicyUnknown,
  kFilesystemRejected,
  kObservationsOnly,
};

template <class PolicyObserver, class FilesystemObserver>
CombinedObservation ObserveCombinedWith(
    std::wstring_view expected_root, std::wstring_view selected_path,
    bool fallback_requested, PolicyObserver policy, FilesystemObserver filesystem) {
  if (Evaluate(expected_root, selected_path, false, fallback_requested) !=
      PathDecision::kLexicalMatchOnly)
    return CombinedObservation::kLexicalRejected;
  const auto before = policy();
  if (before == PolicyObservation::kOverrideObserved)
    return CombinedObservation::kPolicyOverrideObserved;
  if (before != PolicyObservation::kNoValueObserved)
    return CombinedObservation::kPolicyUnknown;
  if (filesystem(expected_root, selected_path) != FilesystemObservation::kObservedOnly)
    return CombinedObservation::kFilesystemRejected;
  // Repeat while the caller's retained handles are still alive. Two snapshots
  // cannot establish that policy remained unchanged between or after queries.
  const auto after = policy();
  if (after == PolicyObservation::kOverrideObserved)
    return CombinedObservation::kPolicyOverrideObserved;
  if (after != PolicyObservation::kNoValueObserved)
    return CombinedObservation::kPolicyUnknown;
  return CombinedObservation::kObservationsOnly;
}

inline CombinedObservation ObserveDevelopmentProfile(
    std::wstring_view expected_root, std::wstring_view selected_path,
    bool fallback_requested) {
  DirectoryHandleRetention retained;
  return ObserveCombinedWith(expected_root, selected_path, fallback_requested,
      ObserveDevelopmentProfilePolicy,
      [&retained](std::wstring_view expected, std::wstring_view selected) {
        return retained.Observe(expected, selected, false, false);
      });
  // Destruction releases all handles. A successful observation is deliberately
  // not a lease, path ownership proof or permission to access profile data.
}
}  // namespace veil::chromium_dev
#endif
