// SPDX-License-Identifier: MPL-2.0
// Synthetic append-only journal fixture. Never access the installed registration.
#ifndef VEIL_CHROMIUM_DEV_CUTOVER_JOURNAL_STORAGE_WIN_H_
#define VEIL_CHROMIUM_DEV_CUTOVER_JOURNAL_STORAGE_WIN_H_
#include "cutover-journal-contract.h"
#include "client-hash-win.h"
#include "payload-object-policy-win.h"
#include <memory>
#include <mutex>

namespace veil::chromium_dev {
enum class JournalStorageObservation {
  kRejected, kUnavailable, kUnsafe, kPending, kInvalidHistory, kConflict,
  kEmpty, kHistoryOnly, kPublishedOnly, kBusy, kLeaseOnly, kBackupOnly, kLegacyBytesOnly, kTargetBytesOnly, kDigestMismatch, kRecoveryReview, kRestoredOnly,
};
namespace journal_storage_detail {
inline constexpr std::array<std::wstring_view, 3> names{
    L"prepared.json", L"registration-replaced.json", L"activation-committed.json"};
inline constexpr std::wstring_view pending = L"record.pending";
inline constexpr std::wstring_view lock_name = L"maintenance.lock";
inline constexpr std::wstring_view registration_name = L"registration-fixture.json";
inline constexpr std::wstring_view backup_name = L"registration-backup.json";
inline constexpr std::wstring_view backup_pending = L"backup.pending";
inline constexpr std::wstring_view restore_marker = L"restore-started.json";
inline constexpr std::wstring_view restore_pending = L"restore-copy.pending";
inline constexpr std::wstring_view displaced_name = L"restore-displaced.json";
inline bool FixtureRoot(std::wstring_view expected, std::wstring_view selected) {
  if (Evaluate(expected, selected, false, false) != PathDecision::kLexicalMatchOnly)
    return false;
  const auto leaf = selected.substr(selected.rfind(L'\\') + 1);
  constexpr std::wstring_view prefix = L"veil-cutover-journal-fixture-";
  if (leaf.substr(0, prefix.size()) != prefix || leaf.size() != prefix.size() + 32)
    return false;
  std::string id;
  for (auto c : leaf.substr(prefix.size())) {
    if (!((c >= L'0' && c <= L'9') || (c >= L'a' && c <= L'f'))) return false;
    id.push_back(static_cast<char>(c));
  }
  return cutover_journal_detail::ValidId(id) &&
      SafeDedicatedPath(std::wstring(selected) + L"\\activation-committed.json");
}
inline std::wstring Path(const std::wstring& root, std::wstring_view leaf) {
  return root + L"\\" + std::wstring(leaf);
}
// Lock file must be provisioned by the fixture owner. Never create, delete,
// truncate or replace it. A no-sharing data handle is a cooperative lease.
class OperationLease {
 public:
  OperationLease() = default;
  OperationLease(const OperationLease&) = delete;
  OperationLease& operator=(const OperationLease&) = delete;
  ~OperationLease() { if (file_ != INVALID_HANDLE_VALUE) CloseHandle(file_); }
  template <class Policy>
  JournalStorageObservation Acquire(const std::wstring& root, HANDLE parent, Policy& policy) {
    using R = JournalStorageObservation;
    if (file_ != INVALID_HANDLE_VALUE) return R::kConflict;
    if (ValidateDirectoryHandle(parent, root) != FilesystemObservation::kObservedOnly ||
        !policy(parent, true)) return R::kUnsafe;
    const auto path = Path(root, lock_name);
    if (!SafeDedicatedPath(path)) return R::kRejected;
    file_ = CreateFileW(path.c_str(), GENERIC_READ, 0, nullptr, OPEN_EXISTING,
        FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
    if (file_ == INVALID_HANDLE_VALUE)
      return GetLastError() == ERROR_SHARING_VIOLATION ? R::kBusy : R::kUnavailable;
    return Recheck(root, parent, policy) ? R::kLeaseOnly : R::kUnsafe;
  }
  template <class Policy>
  bool Recheck(const std::wstring& root, HANDLE parent, Policy& policy) const {
    LARGE_INTEGER size{};
    return file_ != INVALID_HANDLE_VALUE &&
        config_path_detail::ObserveFileShape(file_, Path(root, lock_name)) ==
            ConfigPathObservation::kObservedOnly &&
        GetFileSizeEx(file_, &size) && size.QuadPart == 0 && policy(file_, false) &&
        ValidateDirectoryHandle(parent, root) == FilesystemObservation::kObservedOnly &&
        policy(parent, true);
  }
 private:
  HANDLE file_ = INVALID_HANDLE_VALUE;
};
inline int Slot(CutoverJournalState state) {
  switch (state) {
    case CutoverJournalState::kPrepared: return 0;
    case CutoverJournalState::kRegistrationReplaced: return 1;
    case CutoverJournalState::kActivationCommitted: return 2;
    default: return -1;
  }
}
enum class Presence { kAbsent, kPresent, kUnavailable };
inline Presence Exists(const std::wstring& path) {
  if (GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES) return Presence::kPresent;
  return GetLastError() == ERROR_FILE_NOT_FOUND ? Presence::kAbsent : Presence::kUnavailable;
}
inline bool RestoreArtifactsAbsent(const std::wstring& root) {
  return Exists(Path(root, restore_marker)) == Presence::kAbsent &&
      Exists(Path(root, restore_pending)) == Presence::kAbsent &&
      Exists(Path(root, displaced_name)) == Presence::kAbsent;
}
template <class Policy>
JournalStorageObservation ReadHistory(const std::wstring& root, HANDLE parent,
    CutoverJournalRecord* output, Policy& policy) {
  using R = JournalStorageObservation;
  if (ValidateDirectoryHandle(parent, root) != FilesystemObservation::kObservedOnly ||
      !policy(parent, true)) return R::kUnsafe;
  if (!RestoreArtifactsAbsent(root)) return R::kRecoveryReview;
  const auto pending_state = Exists(Path(root, pending));
  if (pending_state == Presence::kPresent) return R::kPending;
  if (pending_state != Presence::kAbsent) return R::kUnavailable;
  const auto backup_pending_state = Exists(Path(root, backup_pending));
  if (backup_pending_state == Presence::kPresent) return R::kPending;
  if (backup_pending_state != Presence::kAbsent) return R::kUnavailable;
  bool gap = false, found = false;
  CutoverJournalRecord last;
  for (std::size_t i = 0; i < names.size(); ++i) {
    const auto path = Path(root, names[i]);
    const auto presence = Exists(path);
    if (presence == Presence::kAbsent) { gap = true; continue; }
    if (presence != Presence::kPresent) return R::kUnavailable;
    if (gap) return R::kInvalidHistory;
    config_path_detail::Handle file(CreateFileW(path.c_str(), GENERIC_READ,
        FILE_SHARE_READ, nullptr, OPEN_EXISTING,
        FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    if (!file.valid()) return R::kUnavailable;
    if (config_path_detail::ObserveFileShape(file.get(), path) !=
        ConfigPathObservation::kObservedOnly || !policy(file.get(), false)) return R::kUnsafe;
    BY_HANDLE_FILE_INFORMATION before{}, after{};
    LARGE_INTEGER size{};
    if (!GetFileInformationByHandle(file.get(), &before) ||
        !GetFileSizeEx(file.get(), &size) || size.QuadPart <= 0 ||
        size.QuadPart > static_cast<LONGLONG>(kCutoverJournalLimit)) return R::kInvalidHistory;
    std::string bytes;
    if (config_path_detail::ReadBoundedFixture(file.get(), &bytes) !=
        ConfigPathObservation::kContentObservedOnly) return R::kInvalidHistory;
    if (!GetFileInformationByHandle(file.get(), &after) ||
        !client_hash_detail::SameFileInfo(before, after) ||
        config_path_detail::ObserveFileShape(file.get(), path) !=
            ConfigPathObservation::kObservedOnly || !policy(file.get(), false)) return R::kUnsafe;
    CutoverJournalRecord record;
    if (DecodeSyntheticCutoverJournal(bytes, &record) != CutoverJournalResult::kRecordOnly ||
        Slot(record.state) != static_cast<int>(i) ||
        (found && !IsSyntheticCutoverJournalSuccessor(last, record))) return R::kInvalidHistory;
    last = std::move(record);
    found = true;
  }
  if (ValidateDirectoryHandle(parent, root) != FilesystemObservation::kObservedOnly ||
      !policy(parent, true)) return R::kUnsafe;
  if (Exists(Path(root, pending)) != Presence::kAbsent ||
      Exists(Path(root, backup_pending)) != Presence::kAbsent) return R::kPending;
  if (!RestoreArtifactsAbsent(root)) return R::kRecoveryReview;
  if (!found) return R::kEmpty;
  *output = std::move(last);
  return R::kHistoryOnly;
}
inline bool RenameNoReplace(HANDLE file, const std::wstring& destination) {
  alignas(FILE_RENAME_INFO) std::array<unsigned char, 2048> storage{};
  constexpr std::size_t offset = offsetof(FILE_RENAME_INFO, FileName);
  const auto length = destination.size() * sizeof(wchar_t);
  if (length > storage.size() - offset) return false;
  auto* rename = reinterpret_cast<FILE_RENAME_INFO*>(storage.data());
  rename->ReplaceIfExists = FALSE;
  rename->RootDirectory = nullptr;
  rename->FileNameLength = static_cast<DWORD>(length);
  std::memcpy(rename->FileName, destination.data(), length);
  return SetFileInformationByHandle(file, FileRenameInfo, rename,
      static_cast<DWORD>(offset + length)) != FALSE;
}
// Bounded opaque fixture bytes. Check digest and content on the same handle;
// no claim that these bytes form a valid broker registration or signed package.
template <class Policy>
bool ReadRegistrationBytes(HANDLE file, const std::wstring& path, Policy& policy,
    std::string* bytes, std::string* digest) {
  BY_HANDLE_FILE_INFORMATION before{}, after{};
  LARGE_INTEGER size{}, start{};
  std::string candidate, hash, second;
  if (config_path_detail::ObserveFileShape(file, path) != ConfigPathObservation::kObservedOnly ||
      !policy(file, false) || !GetFileInformationByHandle(file, &before) ||
      !GetFileSizeEx(file, &size) || size.QuadPart <= 0 ||
      size.QuadPart > config_path_detail::kConfigFixtureLimit ||
      client_hash_detail::HashFixtureHandle(file, &hash) != ClientHashObservation::kMatchesOnly ||
      !SetFilePointerEx(file, start, nullptr, FILE_BEGIN) ||
      config_path_detail::ReadBoundedFixture(file, &candidate) !=
          ConfigPathObservation::kContentObservedOnly ||
      client_hash_detail::HashFixtureHandle(file, &second) != ClientHashObservation::kMatchesOnly ||
      !SameClientHash(hash, second) || !GetFileInformationByHandle(file, &after) ||
      !client_hash_detail::SameFileInfo(before, after) ||
      config_path_detail::ObserveFileShape(file, path) != ConfigPathObservation::kObservedOnly ||
      !policy(file, false)) return false;
  *bytes = std::move(candidate);
  *digest = std::move(hash);
  return true;
}
// Keep both checked file handles alive through journal publication. A
// mismatch is not permission to repair either file.
template <class Policy>
JournalStorageObservation ClassifyRegistrationHandles(
    HANDLE original, const std::wstring& source, HANDLE backup,
    const std::wstring& target, const CutoverJournalRecord& record, Policy& policy) {
  using R = JournalStorageObservation;
  std::string bytes, digest, saved, saved_hash;
  if (!ReadRegistrationBytes(original, source, policy, &bytes, &digest) ||
      !ReadRegistrationBytes(backup, target, policy, &saved, &saved_hash))
    return R::kUnsafe;
  if (!SameClientHash(saved_hash, record.backup_sha256)) return R::kDigestMismatch;
  if (SameClientHash(digest, record.backup_sha256) && bytes == saved) return R::kLegacyBytesOnly;
  if (SameClientHash(digest, record.target_registration_sha256)) return R::kTargetBytesOnly;
  return R::kDigestMismatch;
}
inline bool NativePolicy(HANDLE handle, bool directory) {
  return ObservePayloadObjectPolicy(handle, directory) == PayloadObjectObservation::kObservedOnly;
}
}  // namespace journal_storage_detail

// Session owns the retained ancestors, checked directory and exclusive lock.
// No raw handles escape. This is a fixture journal lease, not a product
// maintenance/service lease. Do not reenter from policy callbacks.
template <class Policy>
class SyntheticJournalSession {
 public:
  using R = JournalStorageObservation;
  SyntheticJournalSession(std::wstring_view expected, std::wstring_view selected, Policy policy)
      : root_(selected), policy_(std::move(policy)) {
    using namespace journal_storage_detail;
    if (!FixtureRoot(expected, selected)) return;
    if (retained_.Observe(expected, selected, false, false) !=
        FilesystemObservation::kObservedOnly) { status_ = R::kUnsafe; return; }
    parent_.reset(new config_path_detail::Handle(CreateFileW(root_.c_str(),
        READ_CONTROL | FILE_READ_ATTRIBUTES | FILE_LIST_DIRECTORY,
        FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING,
        FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr)));
    if (!parent_->valid()) { status_ = R::kUnavailable; return; }
    status_ = lease_.Acquire(root_, parent_->get(), policy_);
  }
  SyntheticJournalSession(const SyntheticJournalSession&) = delete;
  SyntheticJournalSession& operator=(const SyntheticJournalSession&) = delete;
  R status() const noexcept { return status_; }
  R Read(CutoverJournalRecord* output) {
    std::lock_guard<std::mutex> guard(mutex_);
    if (!output) return R::kRejected;
    if (status_ != R::kLeaseOnly) return status_;
    if (!lease_.Recheck(root_, parent_->get(), policy_)) return R::kUnsafe;
    CutoverJournalRecord candidate;
    const auto result = journal_storage_detail::ReadHistory(root_, parent_->get(), &candidate, policy_);
    if (!lease_.Recheck(root_, parent_->get(), policy_)) return R::kUnsafe;
    if (result == R::kHistoryOnly) *output = std::move(candidate);
    return result;
  }
  // Create a byte-for-byte legacy fixture backup before any journal record.
  // The requested hash is bound to the proposed prepared record, not trusted
  // independently; a later explicit observation binds it to stored history.
  R CreateRegistrationBackup(const CutoverJournalRecord& prepared) {
    using namespace journal_storage_detail;
    std::lock_guard<std::mutex> guard(mutex_);
    if (!cutover_journal_detail::ValidRecord(prepared) ||
        prepared.state != CutoverJournalState::kPrepared) return R::kRejected;
    if (status_ != R::kLeaseOnly) return status_;
    if (!lease_.Recheck(root_, parent_->get(), policy_)) return R::kUnsafe;
    CutoverJournalRecord previous;
    const auto history = ReadHistory(root_, parent_->get(), &previous, policy_);
    if (history != R::kEmpty) return history == R::kHistoryOnly ? R::kConflict : history;
    const auto source = Path(root_, registration_name);
    const auto target = Path(root_, backup_name);
    const auto temp = Path(root_, backup_pending);
    if (!SafeDedicatedPath(source) || !SafeDedicatedPath(target) || !SafeDedicatedPath(temp))
      return R::kRejected;
    if (Exists(target) != Presence::kAbsent) return R::kConflict;
    config_path_detail::Handle original(CreateFileW(source.c_str(), GENERIC_READ,
        FILE_SHARE_READ, nullptr, OPEN_EXISTING,
        FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    if (!original.valid()) return R::kUnavailable;
    std::string bytes, digest;
    if (!ReadRegistrationBytes(original.get(), source, policy_, &bytes, &digest)) return R::kUnsafe;
    if (!SameClientHash(digest, prepared.backup_sha256)) return R::kDigestMismatch;
    config_path_detail::Handle backup(CreateFileW(temp.c_str(), GENERIC_READ | GENERIC_WRITE | DELETE,
        0, nullptr, CREATE_NEW,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_WRITE_THROUGH, nullptr));
    if (!backup.valid()) return R::kConflict;
    if (config_path_detail::ObserveFileShape(backup.get(), temp) != ConfigPathObservation::kObservedOnly ||
        !policy_(backup.get(), false)) return R::kUnsafe;
    DWORD written = 0;
    const DWORD length = static_cast<DWORD>(bytes.size());
    if (!WriteFile(backup.get(), bytes.data(), length, &written, nullptr) ||
        written != length || !FlushFileBuffers(backup.get())) return R::kPending;
    std::string reread, copied_hash, original_again, original_hash;
    if (!ReadRegistrationBytes(backup.get(), temp, policy_, &reread, &copied_hash) ||
        !ReadRegistrationBytes(original.get(), source, policy_, &original_again, &original_hash) ||
        reread != bytes || original_again != bytes || !SameClientHash(digest, copied_hash) ||
        !SameClientHash(digest, original_hash) ||
        !lease_.Recheck(root_, parent_->get(), policy_)) return R::kPending;
    if (!RenameNoReplace(backup.get(), target)) return R::kPending;
    if (!FlushFileBuffers(backup.get()) ||
        config_path_detail::ObserveFileShape(backup.get(), target) != ConfigPathObservation::kObservedOnly ||
        !policy_(backup.get(), false) || !lease_.Recheck(root_, parent_->get(), policy_))
      return R::kUnavailable;
    return R::kBackupOnly;
  }
  // Observe stored history and both files under the same cooperative lease.
  // The result is bytes-only; it is not authorization or activation evidence.
  R VerifyRegistrationArtifacts() {
    using namespace journal_storage_detail;
    std::lock_guard<std::mutex> guard(mutex_);
    if (status_ != R::kLeaseOnly) return status_;
    if (!lease_.Recheck(root_, parent_->get(), policy_)) return R::kUnsafe;
    CutoverJournalRecord record;
    const auto history = ReadHistory(root_, parent_->get(), &record, policy_);
    if (history != R::kHistoryOnly) return history;
    const auto source = Path(root_, registration_name);
    const auto target = Path(root_, backup_name);
    if (!SafeDedicatedPath(source) || !SafeDedicatedPath(target)) return R::kRejected;
    config_path_detail::Handle original(CreateFileW(source.c_str(), GENERIC_READ,
        FILE_SHARE_READ, nullptr, OPEN_EXISTING,
        FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    config_path_detail::Handle backup(CreateFileW(target.c_str(), GENERIC_READ,
        FILE_SHARE_READ, nullptr, OPEN_EXISTING,
        FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    if (!original.valid() || !backup.valid()) return R::kUnavailable;
    const auto observed = ClassifyRegistrationHandles(original.get(), source, backup.get(),
        target, record, policy_);
    return lease_.Recheck(root_, parent_->get(), policy_) ? observed : R::kUnsafe;
  }
  // Fixture only. Evidence is supplied by a test, never inferred from missing
  // markers or the lock. Retain a permanent recovery barrier and displaced
  // bytes; no automatic resume, cleanup, or production rollback.
  R RestoreRegistrationFixture(const CutoverRecoveryEvidence& evidence) {
    using namespace journal_storage_detail;
    using J = CutoverJournalState;
    using D = CutoverRecoveryDecision;
    using S = CutoverRegistrationState;
    std::lock_guard<std::mutex> guard(mutex_);
    if (status_ != R::kLeaseOnly) return status_;
    if (!lease_.Recheck(root_, parent_->get(), policy_)) return R::kUnsafe;
    CutoverJournalRecord record;
    const auto history = ReadHistory(root_, parent_->get(), &record, policy_);
    if (history != R::kHistoryOnly) return history;
    if (record.state != J::kPrepared && record.state != J::kRegistrationReplaced)
      return R::kRecoveryReview;
    const auto source = Path(root_, registration_name);
    const auto saved = Path(root_, backup_name);
    const auto marker_path = Path(root_, restore_marker);
    const auto temp = Path(root_, restore_pending);
    const auto displaced = Path(root_, displaced_name);
    if (!SafeDedicatedPath(source) || !SafeDedicatedPath(saved) ||
        !SafeDedicatedPath(marker_path) || !SafeDedicatedPath(temp) ||
        !SafeDedicatedPath(displaced)) return R::kRejected;
    config_path_detail::Handle original(CreateFileW(source.c_str(), GENERIC_READ | DELETE,
        0, nullptr, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
        nullptr));
    config_path_detail::Handle backup(CreateFileW(saved.c_str(), GENERIC_READ,
        FILE_SHARE_READ, nullptr, OPEN_EXISTING,
        FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    if (!original.valid() || !backup.valid()) return R::kUnavailable;
    const auto observed = ClassifyRegistrationHandles(original.get(), source, backup.get(),
        saved, record, policy_);
    const auto registration = observed == R::kLegacyBytesOnly ? S::kLegacy :
        observed == R::kTargetBytesOnly ? S::kTarget : S::kUnknown;
    const auto decision = DecideCutoverRecovery(record.state, registration, evidence);
    if (decision == D::kVerifyLegacyCleanupCandidate) return R::kLegacyBytesOnly;
    if (decision != D::kRestoreLegacyCandidate) return R::kRecoveryReview;
    std::string marker_bytes, saved_bytes, saved_hash;
    if (EncodeSyntheticCutoverJournal(record, &marker_bytes) != CutoverJournalResult::kRecordOnly ||
        !ReadRegistrationBytes(backup.get(), saved, policy_, &saved_bytes, &saved_hash) ||
        !SameClientHash(saved_hash, record.backup_sha256) ||
        !lease_.Recheck(root_, parent_->get(), policy_) || !RestoreArtifactsAbsent(root_))
      return R::kRecoveryReview;
    auto write_checked = [&](HANDLE file, const std::wstring& path, const std::string& content) {
      if (config_path_detail::ObserveFileShape(file, path) != ConfigPathObservation::kObservedOnly ||
          !policy_(file, false)) return false;
      DWORD written = 0;
      const auto length = static_cast<DWORD>(content.size());
      if (!WriteFile(file, content.data(), length, &written, nullptr) ||
          written != length || !FlushFileBuffers(file)) return false;
      std::string reread, digest;
      return ReadRegistrationBytes(file, path, policy_, &reread, &digest) && reread == content;
    };
    // Once created, even an incomplete marker blocks ordinary journal APIs.
    config_path_detail::Handle marker(CreateFileW(marker_path.c_str(), GENERIC_READ | GENERIC_WRITE,
        0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT |
        FILE_FLAG_WRITE_THROUGH, nullptr));
    if (!marker.valid()) return R::kRecoveryReview;
    if (!write_checked(marker.get(), marker_path, marker_bytes)) return R::kRecoveryReview;
    config_path_detail::Handle copy(CreateFileW(temp.c_str(), GENERIC_READ | GENERIC_WRITE | DELETE,
        0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT |
        FILE_FLAG_WRITE_THROUGH, nullptr));
    if (!copy.valid() || !write_checked(copy.get(), temp, saved_bytes) ||
        ClassifyRegistrationHandles(original.get(), source, backup.get(), saved, record, policy_) !=
            R::kTargetBytesOnly || !lease_.Recheck(root_, parent_->get(), policy_))
      return R::kRecoveryReview;
    // Two no-overwrite renames, NOT an atomic replacement. A crash between
    // them can leave registration absent; preserve marker and both artifacts.
    if (!RenameNoReplace(original.get(), displaced) ||
        config_path_detail::ObserveFileShape(original.get(), displaced) !=
            ConfigPathObservation::kObservedOnly || !policy_(original.get(), false) ||
        !lease_.Recheck(root_, parent_->get(), policy_) ||
        !RenameNoReplace(copy.get(), source) || !FlushFileBuffers(copy.get()))
      return R::kRecoveryReview;
    if (ClassifyRegistrationHandles(copy.get(), source, backup.get(), saved, record, policy_) !=
        R::kLegacyBytesOnly || !lease_.Recheck(root_, parent_->get(), policy_))
      return R::kRecoveryReview;
    return R::kRestoredOnly;
  }
  R Publish(const CutoverJournalRecord& next) {
    using namespace journal_storage_detail;
    std::lock_guard<std::mutex> guard(mutex_);
    std::string bytes;
    if (EncodeSyntheticCutoverJournal(next, &bytes) != CutoverJournalResult::kRecordOnly)
      return R::kRejected;
    const int slot = Slot(next.state);
    if (slot < 0) return R::kRejected;
    if (status_ != R::kLeaseOnly) return status_;
    if (!lease_.Recheck(root_, parent_->get(), policy_)) return R::kUnsafe;
    CutoverJournalRecord previous;
    const auto history = ReadHistory(root_, parent_->get(), &previous, policy_);
    if (history != R::kEmpty && history != R::kHistoryOnly) return history;
    if ((history == R::kEmpty && slot != 0) ||
        (history == R::kHistoryOnly && !IsSyntheticCutoverJournalSuccessor(previous, next)))
      return R::kConflict;
    // Every new journal stage must correspond to the actual registration
    // bytes and an intact legacy backup. No public weaker publish path.
    const auto source = Path(root_, registration_name);
    const auto saved = Path(root_, backup_name);
    if (!SafeDedicatedPath(source) || !SafeDedicatedPath(saved)) return R::kRejected;
    config_path_detail::Handle original(CreateFileW(source.c_str(), GENERIC_READ,
        FILE_SHARE_READ, nullptr, OPEN_EXISTING,
        FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    config_path_detail::Handle backup(CreateFileW(saved.c_str(), GENERIC_READ,
        FILE_SHARE_READ, nullptr, OPEN_EXISTING,
        FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    if (!original.valid() || !backup.valid()) return R::kUnavailable;
    const auto required = slot == 0 ? R::kLegacyBytesOnly : R::kTargetBytesOnly;
    auto artifacts = [&] {
      const auto state = ClassifyRegistrationHandles(original.get(), source, backup.get(),
          saved, next, policy_);
      if (!lease_.Recheck(root_, parent_->get(), policy_)) return R::kUnsafe;
      if (state == R::kLegacyBytesOnly || state == R::kTargetBytesOnly)
        return state == required ? state : R::kConflict;
      return state;
    };
    const auto initial = artifacts();
    if (initial != required) return initial;
    const auto temporary = Path(root_, pending);
    const auto destination = Path(root_, names[static_cast<std::size_t>(slot)]);
    // CREATE_NEW + no-overwrite handle rename. Leave failed pending artifacts
    // for review; never delete or truncate a pre-existing record.
    config_path_detail::Handle file(CreateFileW(temporary.c_str(),
        GENERIC_READ | GENERIC_WRITE | DELETE, 0, nullptr, CREATE_NEW,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_WRITE_THROUGH, nullptr));
    if (!file.valid()) return R::kConflict;
    if (config_path_detail::ObserveFileShape(file.get(), temporary) !=
        ConfigPathObservation::kObservedOnly || !policy_(file.get(), false) ||
        !policy_(parent_->get(), true)) return R::kUnsafe;
    DWORD written = 0;
    const DWORD length = static_cast<DWORD>(bytes.size());
    if (!WriteFile(file.get(), bytes.data(), length, &written, nullptr) ||
        written != length || !FlushFileBuffers(file.get())) return R::kPending;
    LARGE_INTEGER start{};
    std::string reread;
    if (!SetFilePointerEx(file.get(), start, nullptr, FILE_BEGIN) ||
        config_path_detail::ReadBoundedFixture(file.get(), &reread) !=
            ConfigPathObservation::kContentObservedOnly || reread != bytes ||
        config_path_detail::ObserveFileShape(file.get(), temporary) !=
            ConfigPathObservation::kObservedOnly || !policy_(file.get(), false) ||
        ValidateDirectoryHandle(parent_->get(), root_) != FilesystemObservation::kObservedOnly ||
        !policy_(parent_->get(), true)) return R::kPending;
    if (artifacts() != required) return R::kPending;
    if (!RenameNoReplace(file.get(), destination)) return R::kPending;
    if (!FlushFileBuffers(file.get()) ||
        config_path_detail::ObserveFileShape(file.get(), destination) !=
            ConfigPathObservation::kObservedOnly || !policy_(file.get(), false) ||
        !policy_(parent_->get(), true)) return R::kUnavailable; // publication ambiguous
    if (artifacts() != required) return R::kUnavailable; // publication ambiguous
    return R::kPublishedOnly;
  }
 private:
  std::wstring root_;
  Policy policy_;
  DirectoryHandleRetention retained_;
  std::unique_ptr<config_path_detail::Handle> parent_;
  journal_storage_detail::OperationLease lease_;
  R status_ = R::kRejected;
  std::mutex mutex_;
};

template <class Policy>
JournalStorageObservation ObserveSyntheticJournalHistoryWith(
    std::wstring_view expected, std::wstring_view selected,
    CutoverJournalRecord* output, Policy policy) {
  if (!output) return JournalStorageObservation::kRejected;
  SyntheticJournalSession<Policy> session(expected, selected, std::move(policy));
  return session.Read(output);
}
template <class Policy>
JournalStorageObservation PublishSyntheticJournalRecordWith(
    std::wstring_view expected, std::wstring_view selected,
    const CutoverJournalRecord& next, Policy policy) {
  if (!cutover_journal_detail::ValidRecord(next)) return JournalStorageObservation::kRejected;
  SyntheticJournalSession<Policy> session(expected, selected, std::move(policy));
  return session.Publish(next);
}
inline JournalStorageObservation ObserveSyntheticJournalHistory(
    std::wstring_view expected, std::wstring_view selected, CutoverJournalRecord* output) {
  return ObserveSyntheticJournalHistoryWith(expected, selected, output,
      journal_storage_detail::NativePolicy);
}
inline JournalStorageObservation PublishSyntheticJournalRecord(
    std::wstring_view expected, std::wstring_view selected, const CutoverJournalRecord& next) {
  return PublishSyntheticJournalRecordWith(expected, selected, next,
      journal_storage_detail::NativePolicy);
}
}  // namespace veil::chromium_dev
#endif
