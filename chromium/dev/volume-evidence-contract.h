// SPDX-License-Identifier: MPL-2.0
// Synthetic evidence contract only. Booleans are not authenticated evidence.
#ifndef VEIL_CHROMIUM_DEV_VOLUME_EVIDENCE_CONTRACT_H_
#define VEIL_CHROMIUM_DEV_VOLUME_EVIDENCE_CONTRACT_H_
#include "profile-path-contract.h"

namespace veil::chromium_dev {
struct VolumeEvidence {
  bool administrator_config_verified = false;
  bool broker_identity_verified = false;
  bool fresh_unlock_observed = false;
  bool fully_encrypted = false;
  bool protection_on = false;
  bool unlocked = false;
  bool protector_present = false;
  bool no_lock_request_observed = false;
};

enum class VolumeEvidenceResult {
  kPathRejected,
  kMissingEvidence,
  kInvalidVolumeIdentity,
  kVolumeMismatch,
  kEvidenceMatchesOnly,
};

constexpr wchar_t FoldVolumeAscii(wchar_t c) {
  return c >= L'A' && c <= L'Z' ? c + (L'a' - L'A') : c;
}

constexpr bool EqualVolumeAscii(std::wstring_view a, std::wstring_view b) {
  if (a.size() != b.size()) return false;
  for (std::size_t i = 0; i < a.size(); ++i)
    if (FoldVolumeAscii(a[i]) != FoldVolumeAscii(b[i])) return false;
  return true;
}

constexpr bool IsVolumeGuid(std::wstring_view id) {
  constexpr std::wstring_view prefix = L"\\\\?\\Volume{";
  if (id.size() != prefix.size() + 36 + 2 ||
      !EqualVolumeAscii(id.substr(0, prefix.size()), prefix) ||
      id.substr(id.size() - 2) != L"}\\") return false;
  bool nonzero = false;
  for (std::size_t i = 0; i < 36; ++i) {
    const auto c = FoldVolumeAscii(id[prefix.size() + i]);
    if (i == 8 || i == 13 || i == 18 || i == 23) {
      if (c != L'-') return false;
    } else {
      if (!((c >= L'0' && c <= L'9') || (c >= L'a' && c <= L'f'))) return false;
      nonzero |= c != L'0';
    }
  }
  return nonzero;
}

constexpr VolumeEvidenceResult EvaluateVolumeEvidence(
    std::wstring_view registered_root, std::wstring_view selected_root,
    std::wstring_view reported_volume, std::wstring_view observed_volume,
    bool policy_override_present, bool fallback_requested,
    const VolumeEvidence& e) {
  if (Evaluate(registered_root, selected_root, policy_override_present,
               fallback_requested) != PathDecision::kLexicalMatchOnly)
    return VolumeEvidenceResult::kPathRejected;
  if (!(e.administrator_config_verified && e.broker_identity_verified &&
        e.fresh_unlock_observed && e.fully_encrypted && e.protection_on &&
        e.unlocked && e.protector_present && e.no_lock_request_observed))
    return VolumeEvidenceResult::kMissingEvidence;
  if (!IsVolumeGuid(reported_volume) || !IsVolumeGuid(observed_volume))
    return VolumeEvidenceResult::kInvalidVolumeIdentity;
  if (!EqualVolumeAscii(reported_volume, observed_volume))
    return VolumeEvidenceResult::kVolumeMismatch;
  return VolumeEvidenceResult::kEvidenceMatchesOnly;
}
}  // namespace veil::chromium_dev
#endif
