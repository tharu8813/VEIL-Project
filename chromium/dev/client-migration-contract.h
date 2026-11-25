// SPDX-License-Identifier: MPL-2.0
// Synthetic single-client cutover planning contract. No registration mutation.
#ifndef VEIL_CHROMIUM_DEV_CLIENT_MIGRATION_CONTRACT_H_
#define VEIL_CHROMIUM_DEV_CLIENT_MIGRATION_CONTRACT_H_
#include "config-schema.h"

namespace veil::chromium_dev {
struct ClientPin {
  std::wstring image;
  std::string sha256;
};
struct MigrationEvidence {
  bool administrator_approved = false;
  bool registration_verified = false;
  bool current_package_verified = false;
  bool target_package_verified = false;
  bool target_signature_verified = false;
  bool target_hash_verified = false;
  bool all_clients_closed = false;
  bool volume_detached = false;
  bool service_stopped_disabled = false;
  bool backup_verified = false;
  bool journal_ready = false;
  bool no_unresolved_update = false;
};
enum class MigrationPlanResult {
  kInvalidPin, kWrongCurrentPin, kSameIdentity, kMissingEvidence,
  kCandidateOnly,
};

inline bool ValidClientHash(std::string_view hash) {
  if (hash.size() != 64) return false;
  bool nonzero = false;
  for (const char c : hash) {
    if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') ||
          (c >= 'A' && c <= 'F'))) return false;
    nonzero |= c != '0';
  }
  return nonzero;
}
inline bool SameClientHash(std::string_view a, std::string_view b) {
  if (a.size() != b.size()) return false;
  for (std::size_t i = 0; i < a.size(); ++i)
    if (FoldVolumeAscii(static_cast<wchar_t>(a[i])) !=
        FoldVolumeAscii(static_cast<wchar_t>(b[i]))) return false;
  return true;
}
inline bool ValidClientPin(const ClientPin& pin) {
  return SafeConfigPath(pin.image) && pin.image.size() > 4 &&
      EqualVolumeAscii(std::wstring_view(pin.image).substr(pin.image.size() - 4), L".exe") &&
      ValidClientHash(pin.sha256);
}
inline MigrationPlanResult PlanSingleClientReplacement(
    const ClientPin& registered, const ClientPin& expected_current,
    const ClientPin& target, const MigrationEvidence& evidence,
    ClientPin* candidate) {
  if (!candidate || !ValidClientPin(registered) ||
      !ValidClientPin(expected_current) || !ValidClientPin(target))
    return MigrationPlanResult::kInvalidPin;
  // Exact path spelling is deliberate: no normalization or alias equivalence.
  if (registered.image != expected_current.image ||
      !SameClientHash(registered.sha256, expected_current.sha256))
    return MigrationPlanResult::kWrongCurrentPin;
  if (EqualVolumeAscii(registered.image, target.image) ||
      SameClientHash(registered.sha256, target.sha256))
    return MigrationPlanResult::kSameIdentity;
  if (!(evidence.administrator_approved && evidence.registration_verified &&
        evidence.current_package_verified && evidence.target_package_verified &&
        evidence.target_signature_verified && evidence.target_hash_verified &&
        evidence.all_clients_closed && evidence.volume_detached &&
        evidence.service_stopped_disabled && evidence.backup_verified &&
        evidence.journal_ready && evidence.no_unresolved_update))
    return MigrationPlanResult::kMissingEvidence;
  *candidate = target;
  return MigrationPlanResult::kCandidateOnly;
  // Booleans are synthetic, not proof. No I/O, SCM operation, configuration
  // publication, launch authorization or rollback decision.
}
}  // namespace veil::chromium_dev
#endif
