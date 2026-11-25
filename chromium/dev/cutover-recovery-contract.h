// SPDX-License-Identifier: MPL-2.0
// Pure crash-recovery decision contract. No filesystem, service or config mutation.
#ifndef VEIL_CHROMIUM_DEV_CUTOVER_RECOVERY_CONTRACT_H_
#define VEIL_CHROMIUM_DEV_CUTOVER_RECOVERY_CONTRACT_H_
#include "client-migration-contract.h"

namespace veil::chromium_dev {
enum class CutoverJournalState {
  kAbsent, kPrepared, kRegistrationReplaced, kActivationCommitted, kInvalid,
};
enum class CutoverRegistrationState {
  kLegacy, kTarget, kUnknown,
};
enum class CutoverRecoveryDecision {
  kAdministratorReview, kNoTransaction, kVerifyLegacyCleanupCandidate,
  kRestoreLegacyCandidate, kVerifyForwardRecoveryCandidate,
};
struct CutoverRecoveryEvidence {
  bool exclusive_maintenance = false;
  bool administrator_approved = false;
  bool journal_authenticated = false;
  bool registration_authenticated = false;
  bool legacy_backup_verified = false;
  bool legacy_package_verified = false;
  bool target_package_verified = false;
  bool all_clients_closed = false;
  bool volume_detached = false;
  bool service_stopped_disabled = false;
  bool no_unresolved_update = false;
  bool activation_excluded = false;
};
inline CutoverRegistrationState ClassifyCutoverRegistration(
    const ClientPin& registered, const ClientPin& legacy, const ClientPin& target) {
  if (!ValidClientPin(registered) || !ValidClientPin(legacy) || !ValidClientPin(target) ||
      EqualVolumeAscii(legacy.image, target.image) || SameClientHash(legacy.sha256, target.sha256))
    return CutoverRegistrationState::kUnknown;
  if (registered.image == legacy.image && SameClientHash(registered.sha256, legacy.sha256))
    return CutoverRegistrationState::kLegacy;
  if (registered.image == target.image && SameClientHash(registered.sha256, target.sha256))
    return CutoverRegistrationState::kTarget;
  return CutoverRegistrationState::kUnknown;
}
inline CutoverRecoveryDecision DecideCutoverRecovery(
    CutoverJournalState journal, CutoverRegistrationState registration,
    const CutoverRecoveryEvidence& e) {
  using J = CutoverJournalState;
  using R = CutoverRegistrationState;
  using D = CutoverRecoveryDecision;
  if (!(e.exclusive_maintenance && e.administrator_approved &&
        e.registration_authenticated && e.all_clients_closed &&
        e.volume_detached && e.service_stopped_disabled && e.no_unresolved_update))
    return D::kAdministratorReview;
  if (registration != R::kLegacy && registration != R::kTarget)
    return D::kAdministratorReview;
  if (journal == J::kAbsent) {
    // No journal is not proof that a target registration is committed.
    return registration == R::kLegacy && e.legacy_package_verified && e.activation_excluded
        ? D::kNoTransaction : D::kAdministratorReview;
  }
  if (!e.journal_authenticated) return D::kAdministratorReview;
  if (journal == J::kActivationCommitted) {
    // Durable activation commit is an irreversible recovery boundary, even
    // when the service is currently stopped and the volume detached.
    return registration == R::kTarget && e.target_package_verified
        ? D::kVerifyForwardRecoveryCandidate : D::kAdministratorReview;
  }
  if (journal != J::kPrepared && journal != J::kRegistrationReplaced)
    return D::kAdministratorReview;
  if (!(e.activation_excluded && e.legacy_backup_verified && e.legacy_package_verified))
    return D::kAdministratorReview;
  // A crash can occur between replacement and recording it, or after a prior
  // recovery restored the legacy bytes. Decisions are idempotent candidates.
  return registration == R::kLegacy ? D::kVerifyLegacyCleanupCandidate
                                    : D::kRestoreLegacyCandidate;
}
}  // namespace veil::chromium_dev
#endif
