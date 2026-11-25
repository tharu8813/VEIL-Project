// SPDX-License-Identifier: MPL-2.0
#include "../../chromium/dev/cutover-recovery-contract.h"
#include <array>
#include <iostream>
using namespace veil::chromium_dev;
int main() {
  using J = CutoverJournalState;
  using R = CutoverRegistrationState;
  using D = CutoverRecoveryDecision;
  const ClientPin legacy{L"C:\\Program Files\\VEIL\\Desktop\\Veil.Desktop.exe", std::string(64, 'a')};
  const ClientPin target{L"C:\\Program Files\\VEIL\\Chromium\\client-fixture.exe", std::string(64, 'b')};
  if (ClassifyCutoverRegistration(legacy, legacy, target) != R::kLegacy ||
      ClassifyCutoverRegistration(target, legacy, target) != R::kTarget ||
      ClassifyCutoverRegistration({legacy.image, target.sha256}, legacy, target) != R::kUnknown ||
      ClassifyCutoverRegistration(legacy, legacy, legacy) != R::kUnknown ||
      ClassifyCutoverRegistration({L"relative.exe", legacy.sha256}, legacy, target) != R::kUnknown)
    return 1;
  const std::array<bool CutoverRecoveryEvidence::*, 12> members{
    &CutoverRecoveryEvidence::exclusive_maintenance,
    &CutoverRecoveryEvidence::administrator_approved,
    &CutoverRecoveryEvidence::journal_authenticated,
    &CutoverRecoveryEvidence::registration_authenticated,
    &CutoverRecoveryEvidence::legacy_backup_verified,
    &CutoverRecoveryEvidence::legacy_package_verified,
    &CutoverRecoveryEvidence::target_package_verified,
    &CutoverRecoveryEvidence::all_clients_closed,
    &CutoverRecoveryEvidence::volume_detached,
    &CutoverRecoveryEvidence::service_stopped_disabled,
    &CutoverRecoveryEvidence::no_unresolved_update,
    &CutoverRecoveryEvidence::activation_excluded};
  unsigned combinations = 0;
  for (unsigned mask = 0; mask < (1u << members.size()); ++mask) {
    CutoverRecoveryEvidence e;
    for (unsigned bit = 0; bit < members.size(); ++bit)
      e.*members[bit] = (mask & (1u << bit)) != 0;
    for (auto j : {J::kAbsent, J::kPrepared, J::kRegistrationReplaced, J::kActivationCommitted, J::kInvalid})
      for (auto r : {R::kLegacy, R::kTarget, R::kUnknown}) {
        ++combinations;
        // Independent required-evidence masks.
        constexpr unsigned common = 1u | 2u | 8u | 128u | 256u | 512u | 1024u;
        constexpr unsigned rollback = common | 4u | 16u | 32u | 2048u;
        constexpr unsigned forward = common | 4u | 64u;
        D expected = D::kAdministratorReview;
        if (j == J::kAbsent && r == R::kLegacy &&
            (mask & (common | 32u | 2048u)) == (common | 32u | 2048u))
          expected = D::kNoTransaction;
        if ((j == J::kPrepared || j == J::kRegistrationReplaced) && (mask & rollback) == rollback) {
          if (r == R::kLegacy) expected = D::kVerifyLegacyCleanupCandidate;
          if (r == R::kTarget) expected = D::kRestoreLegacyCandidate;
        }
        if (j == J::kActivationCommitted && r == R::kTarget && (mask & forward) == forward)
          expected = D::kVerifyForwardRecoveryCandidate;
        const auto actual = DecideCutoverRecovery(j, r, e);
        if (actual != expected) return 2;
        if (j == J::kActivationCommitted &&
            (actual == D::kRestoreLegacyCandidate || actual == D::kVerifyLegacyCleanupCandidate))
          return 3;
      }
  }
  CutoverRecoveryEvidence all;
  for (const auto member : members) all.*member = true;
  if (DecideCutoverRecovery(static_cast<J>(99), R::kTarget, all) != D::kAdministratorReview ||
      DecideCutoverRecovery(J::kPrepared, static_cast<R>(99), all) != D::kAdministratorReview)
    return 4;
  std::cout << "PASS: " << combinations
            << " synthetic crash-recovery combinations; no downgrade after activation commit; no mutations\n";
}
