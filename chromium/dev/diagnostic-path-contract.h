// SPDX-License-Identifier: MPL-2.0
// Pure lexical planning for synthetic diagnostic data. No access or creation.
#ifndef VEIL_CHROMIUM_DEV_DIAGNOSTIC_PATH_CONTRACT_H_
#define VEIL_CHROMIUM_DEV_DIAGNOSTIC_PATH_CONTRACT_H_
#include "profile-path-contract.h"
#include <string>

namespace veil::chromium_dev {
enum class DiagnosticKind { kCrashDump, kWatcher, kLocalTrace };
enum class DiagnosticPlanResult { kRejected, kLexicalCandidateOnly };

inline DiagnosticPlanResult PlanDiagnosticPath(
    std::wstring_view expected_root, std::wstring_view selected_root,
    DiagnosticKind kind, bool policy_override_present, bool fallback_requested,
    std::wstring* output) {
  if (!output || Evaluate(expected_root, selected_root, policy_override_present,
                          fallback_requested) != PathDecision::kLexicalMatchOnly)
    return DiagnosticPlanResult::kRejected;
  std::wstring_view suffix;
  switch (kind) {
    case DiagnosticKind::kCrashDump: suffix = L"\\Crashpad"; break;
    case DiagnosticKind::kWatcher: suffix = L"\\Diagnostics"; break;
    case DiagnosticKind::kLocalTrace: suffix = L"\\Local_Traces"; break;
    default: return DiagnosticPlanResult::kRejected;
  }
  // Copy only after validating the entire result. The caller owns the root;
  // this cannot prove that the directory exists or is a trusted volume.
  std::wstring candidate(selected_root);
  candidate.append(suffix);
  if (Evaluate(candidate, candidate, false, false) != PathDecision::kLexicalMatchOnly)
    return DiagnosticPlanResult::kRejected;
  *output = candidate;
  return DiagnosticPlanResult::kLexicalCandidateOnly;
}
}  // namespace veil::chromium_dev
#endif
