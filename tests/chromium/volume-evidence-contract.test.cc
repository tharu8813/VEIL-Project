// SPDX-License-Identifier: MPL-2.0
#include "../../chromium/dev/volume-evidence-contract.h"
#include <array>
#include <iostream>
using namespace veil::chromium_dev;

int main() {
  constexpr auto root = L"D:\\veil-dev\\Synthetic_Profile";
  constexpr auto guid = L"\\\\?\\Volume{a1234567-1234-1234-1234-123456789abc}\\";
  constexpr auto upper = L"\\\\?\\VOLUME{A1234567-1234-1234-1234-123456789ABC}\\";
  constexpr auto other = L"\\\\?\\Volume{b1234567-1234-1234-1234-123456789abc}\\";
  constexpr std::array<bool VolumeEvidence::*, 8> flags{
      &VolumeEvidence::administrator_config_verified,
      &VolumeEvidence::broker_identity_verified,
      &VolumeEvidence::fresh_unlock_observed,
      &VolumeEvidence::fully_encrypted,
      &VolumeEvidence::protection_on,
      &VolumeEvidence::unlocked,
      &VolumeEvidence::protector_present,
      &VolumeEvidence::no_lock_request_observed};
  VolumeEvidence evidence;
  for (unsigned mask = 0; mask < 256; ++mask) {
    for (unsigned i = 0; i < flags.size(); ++i)
      evidence.*flags[i] = (mask & (1u << i)) != 0;
    const auto expected = mask == 255 ? VolumeEvidenceResult::kEvidenceMatchesOnly
                                     : VolumeEvidenceResult::kMissingEvidence;
    if (EvaluateVolumeEvidence(root, root, guid, upper, false, false, evidence) != expected)
      return 1;
  }
  if (EvaluateVolumeEvidence(root, root, guid, other, false, false, evidence) !=
      VolumeEvidenceResult::kVolumeMismatch) return 2;
  for (const auto* invalid : {L"", L"D:\\", L"\\\\?\\Volume{a1234567-1234-1234-1234-123456789abc}",
      L"\\\\?\\Volume{g1234567-1234-1234-1234-123456789abc}\\",
      L"\\\\?\\Volume{00000000-0000-0000-0000-000000000000}\\",
      L"\\\\?\\Volume{a1234567-1234-1234-1234-123456789abc}\\child"}) {
    if (EvaluateVolumeEvidence(root, root, invalid, guid, false, false, evidence) !=
        VolumeEvidenceResult::kInvalidVolumeIdentity ||
        EvaluateVolumeEvidence(root, root, guid, invalid, false, false, evidence) !=
        VolumeEvidenceResult::kInvalidVolumeIdentity) return 3;
  }
  if (EvaluateVolumeEvidence(root, L"D:\\different", guid, guid, false, false, evidence) !=
      VolumeEvidenceResult::kPathRejected ||
      EvaluateVolumeEvidence(root, root, guid, guid, true, false, evidence) !=
      VolumeEvidenceResult::kPathRejected ||
      EvaluateVolumeEvidence(root, root, guid, guid, false, true, evidence) !=
      VolumeEvidenceResult::kPathRejected) return 4;
  std::cout << "PASS: 256 synthetic evidence combinations, GUID shape and identity, path rejection; no authentication or launch approval\n";
}
