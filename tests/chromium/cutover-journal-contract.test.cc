// SPDX-License-Identifier: MPL-2.0
#include "../../chromium/dev/cutover-journal-contract.h"
#include <iostream>
using namespace veil::chromium_dev;
int main() {
  using J = CutoverJournalState;
  using R = CutoverJournalResult;
  CutoverJournalRecord record{std::string(32, 'a'), J::kPrepared,
      {L"C:\\Program Files\\VEIL\\Desktop\\Veil.Desktop.exe", std::string(64, 'b')},
      {L"C:\\Program Files\\VEIL\\Chromium\\client-fixture.exe", std::string(64, 'c')},
      std::string(64, 'd'), std::string(64, 'e')};
  std::string encoded;
  if (EncodeSyntheticCutoverJournal(record, &encoded) != R::kRecordOnly) return 1;
  CutoverJournalRecord decoded;
  if (DecodeSyntheticCutoverJournal(encoded, &decoded) != R::kRecordOnly ||
      decoded.state != J::kPrepared || decoded.transaction_id != record.transaction_id ||
      decoded.legacy.image != record.legacy.image || decoded.target.image != record.target.image ||
      decoded.legacy.sha256 != record.legacy.sha256 || decoded.target.sha256 != record.target.sha256 ||
      decoded.backup_sha256 != record.backup_sha256 ||
      decoded.target_registration_sha256 != record.target_registration_sha256) return 2;
  auto unchanged = [&] {
    std::string check;
    return EncodeSyntheticCutoverJournal(decoded, &check) == R::kRecordOnly && check == encoded;
  };
  for (std::size_t n = 0; n < encoded.size(); ++n)
    if (DecodeSyntheticCutoverJournal(std::string_view(encoded).substr(0, n), &decoded) ==
        R::kRecordOnly || !unchanged()) return 3;
  auto mutate = [&](std::string_view from, std::string_view to) {
    auto s = encoded;
    s.replace(s.find(from), from.size(), to);
    return s;
  };
  for (const auto& bad : {encoded + encoded, " " + encoded, encoded + " ",
      mutate("\"format\":1", "\"format\":2"), mutate("\"format\":1", "\"format\":01"),
      mutate("\"format\":1", "\"state\":\"prepared\""),
      mutate("\"format\":1", "\"unknown\":1"), mutate("prepared", "absent"),
      mutate(record.transaction_id, std::string(32, '0')),
      mutate(record.target.sha256, record.legacy.sha256),
      mutate(record.backup_sha256, record.target_registration_sha256),
      mutate("client-fixture.exe", "NUL.exe"),
      std::string("\xef\xbb\xbf") + encoded,
      std::string(kCutoverJournalLimit + 1, 'x')}) {
    if (DecodeSyntheticCutoverJournal(bad, &decoded) == R::kRecordOnly || !unchanged()) return 4;
  }
  for (auto from : {J::kAbsent, J::kPrepared, J::kRegistrationReplaced, J::kActivationCommitted, J::kInvalid})
    for (auto to : {J::kAbsent, J::kPrepared, J::kRegistrationReplaced, J::kActivationCommitted, J::kInvalid}) {
      auto a = record, b = record;
      a.state = from; b.state = to;
      const bool allowed = (from == J::kPrepared && to == J::kRegistrationReplaced) ||
          (from == J::kRegistrationReplaced && to == J::kActivationCommitted);
      if (IsSyntheticCutoverJournalSuccessor(a, b) != allowed) return 5;
    }
  auto next = record;
  next.state = J::kRegistrationReplaced;
  for (auto member : {&CutoverJournalRecord::transaction_id, &CutoverJournalRecord::backup_sha256,
                      &CutoverJournalRecord::target_registration_sha256}) {
    auto changed = next;
    (changed.*member)[0] = 'f';
    if (IsSyntheticCutoverJournalSuccessor(record, changed)) return 6;
  }
  next.target.image = L"C:\\other.exe";
  if (IsSyntheticCutoverJournalSuccessor(record, next)) return 7;
  auto bad = record;
  bad.state = J::kInvalid;
  std::string preserved = "old";
  if (EncodeSyntheticCutoverJournal(bad, &preserved) != R::kRejected || preserved != "old" ||
      DecodeSyntheticCutoverJournal(encoded, nullptr) != R::kRejected) return 8;
  std::cout << "PASS: canonical journal roundtrip, truncation/invalid fields, immutable transaction binding and all 25 state transitions; no durable storage or authentication\n";
}
