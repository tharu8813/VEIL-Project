// SPDX-License-Identifier: MPL-2.0
// Read-only observation of an existing synthetic diagnostic directory.
// No write, path exposure, crash configuration or launch authorization.
#ifndef VEIL_CHROMIUM_DEV_DIAGNOSTIC_OBSERVATION_WIN_H_
#define VEIL_CHROMIUM_DEV_DIAGNOSTIC_OBSERVATION_WIN_H_
#include "diagnostic-path-contract.h"
#include "profile-combined-win.h"

namespace veil::chromium_dev {
template <class PolicyObserver, class FilesystemObserver>
CombinedObservation ObserveDiagnosticWith(
    std::wstring_view expected_root, std::wstring_view selected_root,
    DiagnosticKind kind, bool fallback_requested, PolicyObserver policy,
    FilesystemObserver filesystem) {
  std::wstring candidate;
  if (PlanDiagnosticPath(expected_root, selected_root, kind, false,
                         fallback_requested, &candidate) !=
      DiagnosticPlanResult::kLexicalCandidateOnly)
    return CombinedObservation::kLexicalRejected;
  // Plan already compared expected and selected roots. The full child path
  // includes the root, so the retention observer also checks its ancestors.
  return ObserveCombinedWith(candidate, candidate, false, policy, filesystem);
}

inline CombinedObservation ObserveDevelopmentDiagnostic(
    std::wstring_view expected_root, std::wstring_view selected_root,
    DiagnosticKind kind, bool fallback_requested) {
  DirectoryHandleRetention retained;
  return ObserveDiagnosticWith(expected_root, selected_root, kind,
      fallback_requested, ObserveDevelopmentProfilePolicy,
      [&retained](std::wstring_view expected, std::wstring_view selected) {
        return retained.Observe(expected, selected, false, false);
      });
  // Handles are released on return. No live write permit is supplied.
}
}  // namespace veil::chromium_dev
#endif
