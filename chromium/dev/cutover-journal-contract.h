// SPDX-License-Identifier: MPL-2.0
// Synthetic journal record codec and monotonic transition contract; no I/O.
#ifndef VEIL_CHROMIUM_DEV_CUTOVER_JOURNAL_CONTRACT_H_
#define VEIL_CHROMIUM_DEV_CUTOVER_JOURNAL_CONTRACT_H_
#include "cutover-recovery-contract.h"

namespace veil::chromium_dev {
inline constexpr std::size_t kCutoverJournalLimit = 8192;
struct CutoverJournalRecord {
  std::string transaction_id;
  CutoverJournalState state = CutoverJournalState::kInvalid;
  ClientPin legacy;
  ClientPin target;
  std::string backup_sha256;
  std::string target_registration_sha256;
};
enum class CutoverJournalResult { kRejected, kMalformed, kRecordOnly };
namespace cutover_journal_detail {
inline std::string_view StateName(CutoverJournalState state) {
  switch (state) {
    case CutoverJournalState::kPrepared: return "prepared";
    case CutoverJournalState::kRegistrationReplaced: return "registration-replaced";
    case CutoverJournalState::kActivationCommitted: return "activation-committed";
    default: return {};
  }
}
inline bool ValidId(std::string_view id) {
  if (id.size() != 32) return false;
  bool nonzero = false;
  for (auto c : id) {
    if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
    nonzero |= c != '0';
  }
  return nonzero;
}
inline bool ValidRecord(const CutoverJournalRecord& record) {
  return ValidId(record.transaction_id) && !StateName(record.state).empty() &&
      ClassifyCutoverRegistration(record.legacy, record.legacy, record.target) ==
          CutoverRegistrationState::kLegacy &&
      ValidClientHash(record.backup_sha256) &&
      ValidClientHash(record.target_registration_sha256) &&
      !SameClientHash(record.backup_sha256, record.target_registration_sha256);
}
inline std::string QuotePath(std::wstring_view path) {
  std::string out = "\"";
  for (auto c : path) {
    if (c == L'\\') out.push_back('\\');
    out.push_back(static_cast<char>(c)); // validated ASCII path subset
  }
  out.push_back('"');
  return out;
}
}  // namespace cutover_journal_detail
inline CutoverJournalResult EncodeSyntheticCutoverJournal(
    const CutoverJournalRecord& record, std::string* output) {
  using namespace cutover_journal_detail;
  if (!output || !ValidRecord(record)) return CutoverJournalResult::kRejected;
  *output = "{\"format\":1,\"transactionId\":\"" + record.transaction_id +
      "\",\"state\":\"" + std::string(StateName(record.state)) +
      "\",\"legacyImage\":" + QuotePath(record.legacy.image) +
      ",\"legacySha256\":\"" + record.legacy.sha256 +
      "\",\"targetImage\":" + QuotePath(record.target.image) +
      ",\"targetSha256\":\"" + record.target.sha256 +
      "\",\"backupSha256\":\"" + record.backup_sha256 +
      "\",\"targetRegistrationSha256\":\"" + record.target_registration_sha256 + "\"}\n";
  return CutoverJournalResult::kRecordOnly;
}
inline CutoverJournalResult DecodeSyntheticCutoverJournal(
    std::string_view bytes, CutoverJournalRecord* output) {
  if (!output || bytes.empty() || bytes.size() > kCutoverJournalLimit)
    return CutoverJournalResult::kRejected;
  status_wire_detail::Reader reader(bytes);
  if (!reader.Take('{')) return CutoverJournalResult::kMalformed;
  constexpr std::array<std::string_view, 9> keys{
      "format", "transactionId", "state", "legacyImage", "legacySha256",
      "targetImage", "targetSha256", "backupSha256", "targetRegistrationSha256"};
  unsigned seen = 0;
  CutoverJournalRecord candidate;
  for (std::size_t n = 0; n < keys.size(); ++n) {
    if (n && !reader.Take(',')) return CutoverJournalResult::kMalformed;
    std::string key, value;
    if (!reader.String(&key) || !reader.Take(':')) return CutoverJournalResult::kMalformed;
    std::size_t i = 0;
    while (i < keys.size() && key != keys[i]) ++i;
    if (i == keys.size() || (seen & (1u << i))) return CutoverJournalResult::kMalformed;
    seen |= 1u << i;
    if (i == 0) {
      std::uint64_t format = 0;
      if (!reader.Unsigned(&format) || format != 1) return CutoverJournalResult::kMalformed;
      continue;
    }
    if (!reader.String(&value)) return CutoverJournalResult::kMalformed;
    switch (i) {
      case 1: candidate.transaction_id = std::move(value); break;
      case 2:
        if (value == "prepared") candidate.state = CutoverJournalState::kPrepared;
        else if (value == "registration-replaced") candidate.state = CutoverJournalState::kRegistrationReplaced;
        else if (value == "activation-committed") candidate.state = CutoverJournalState::kActivationCommitted;
        else return CutoverJournalResult::kMalformed;
        break;
      case 3: candidate.legacy.image.assign(value.begin(), value.end()); break;
      case 4: candidate.legacy.sha256 = std::move(value); break;
      case 5: candidate.target.image.assign(value.begin(), value.end()); break;
      case 6: candidate.target.sha256 = std::move(value); break;
      case 7: candidate.backup_sha256 = std::move(value); break;
      case 8: candidate.target_registration_sha256 = std::move(value); break;
      default: return CutoverJournalResult::kMalformed;
    }
  }
  if (seen != 511u || !reader.Take('}') || !reader.End())
    return CutoverJournalResult::kMalformed;
  if (!cutover_journal_detail::ValidRecord(candidate)) return CutoverJournalResult::kRejected;
  // Canonical byte encoding only: reject alternate escapes, whitespace, case
  // or field order representations, BOM and incomplete final newline.
  std::string canonical;
  if (EncodeSyntheticCutoverJournal(candidate, &canonical) != CutoverJournalResult::kRecordOnly ||
      bytes != canonical) return CutoverJournalResult::kMalformed;
  *output = std::move(candidate);
  return CutoverJournalResult::kRecordOnly;
}
inline bool IsSyntheticCutoverJournalSuccessor(const CutoverJournalRecord& previous,
                                               const CutoverJournalRecord& next) {
  using J = CutoverJournalState;
  if (!cutover_journal_detail::ValidRecord(previous) ||
      !cutover_journal_detail::ValidRecord(next) ||
      previous.transaction_id != next.transaction_id ||
      previous.legacy.image != next.legacy.image ||
      previous.target.image != next.target.image ||
      previous.legacy.sha256 != next.legacy.sha256 ||
      previous.target.sha256 != next.target.sha256 ||
      previous.backup_sha256 != next.backup_sha256 ||
      previous.target_registration_sha256 != next.target_registration_sha256)
    return false;
  return (previous.state == J::kPrepared && next.state == J::kRegistrationReplaced) ||
      (previous.state == J::kRegistrationReplaced && next.state == J::kActivationCommitted);
  // No skipped stages, same-state rewrite, or post-commit downgrade.
}
}  // namespace veil::chromium_dev
#endif
