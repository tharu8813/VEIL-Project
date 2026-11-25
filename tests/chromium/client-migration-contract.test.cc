// SPDX-License-Identifier: MPL-2.0
#include "../../chromium/dev/client-migration-contract.h"
#include <iostream>
using namespace veil::chromium_dev;

int main() {
  const ClientPin current{L"C:\\Program Files\\VEIL\\Desktop\\Veil.Desktop.exe",
                          std::string(64, 'a')};
  const ClientPin next{L"C:\\Program Files\\VEIL Chromium Dev\\veil-dev.exe",
                       std::string(64, 'b')};
  const ClientPin sentinel{L"unchanged", "unchanged"};
  constexpr std::array<bool MigrationEvidence::*, 12> flags{
      &MigrationEvidence::administrator_approved,
      &MigrationEvidence::registration_verified,
      &MigrationEvidence::current_package_verified,
      &MigrationEvidence::target_package_verified,
      &MigrationEvidence::target_signature_verified,
      &MigrationEvidence::target_hash_verified,
      &MigrationEvidence::all_clients_closed,
      &MigrationEvidence::volume_detached,
      &MigrationEvidence::service_stopped_disabled,
      &MigrationEvidence::backup_verified,
      &MigrationEvidence::journal_ready,
      &MigrationEvidence::no_unresolved_update};
  unsigned accepted = 0;
  for (unsigned mask = 0; mask < (1u << flags.size()); ++mask) {
    MigrationEvidence e;
    for (std::size_t i = 0; i < flags.size(); ++i) e.*flags[i] = (mask & (1u << i)) != 0;
    ClientPin output = sentinel;
    const auto result = PlanSingleClientReplacement(current, current, next, e, &output);
    if (mask == 4095) {
      if (result != MigrationPlanResult::kCandidateOnly ||
          output.image != next.image || output.sha256 != next.sha256) return 1;
      ++accepted;
    } else if (result != MigrationPlanResult::kMissingEvidence ||
               output.image != sentinel.image || output.sha256 != sentinel.sha256) return 2;
  }
  if (accepted != 1) return 3;
  MigrationEvidence e;
  for (auto flag : flags) e.*flag = true;
  ClientPin output = sentinel;
  auto rejected = [&](const ClientPin& registered, const ClientPin& expected,
                      const ClientPin& target, MigrationPlanResult result) {
    return PlanSingleClientReplacement(registered, expected, target, e, &output) == result &&
        output.image == sentinel.image && output.sha256 == sentinel.sha256;
  };
  if (!rejected(current, next, next, MigrationPlanResult::kWrongCurrentPin) ||
      !rejected(current, current, current, MigrationPlanResult::kSameIdentity) ||
      !rejected(current, current, {next.image, current.sha256},
                MigrationPlanResult::kSameIdentity) ||
      !rejected(current, current, {L"c:\\program files\\veil\\desktop\\veil.desktop.EXE", next.sha256},
                MigrationPlanResult::kSameIdentity)) return 4;
  for (const auto& pin : {ClientPin{next.image, std::string(64, '0')},
      ClientPin{next.image, std::string(63, 'a')},
      ClientPin{next.image, std::string(64, 'z')},
      ClientPin{L"relative.exe", next.sha256},
      ClientPin{L"C:\\VEIL\\..\\veil.exe", next.sha256},
      ClientPin{L"C:\\VEIL\\veil.exe:stream", next.sha256},
      ClientPin{L"C:\\VEIL\\veil.dll", next.sha256}})
    if (!rejected(current, current, pin, MigrationPlanResult::kInvalidPin)) return 5;
  if (PlanSingleClientReplacement(current, current, next, e, nullptr) !=
      MigrationPlanResult::kInvalidPin) return 6;
  ClientPin upper = current;
  upper.sha256 = std::string(64, 'A');
  if (PlanSingleClientReplacement(current, upper, next, e, &output) !=
      MigrationPlanResult::kCandidateOnly) return 7;
  std::cout << "PASS: 4096 synthetic cutover evidence combinations, single pin and invalid identity rejection; no registration mutation or authorization\n";
}
