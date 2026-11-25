// SPDX-License-Identifier: MPL-2.0
// Synthetic append-only journal fixture. Never access the installed registration.
#ifndef VEIL_CHROMIUM_DEV_CUTOVER_JOURNAL_STORAGE_WIN_H_
#define VEIL_CHROMIUM_DEV_CUTOVER_JOURNAL_STORAGE_WIN_H_
#include "cutover-journal-contract.h"
#include "client-hash-win.h"
#include "payload-object-policy-win.h"

namespace veil::chromium_dev {
enum class JournalStorageObservation {
  kRejected, kUnavailable, kUnsafe, kPending, kInvalidHistory, kConflict,
  kEmpty, kHistoryOnly, kPublishedOnly, kBusy,
};
namespace journal_storage_detail {
inline constexpr std::array<std::wstring_view, 3> names{
    L"prepared.json", L"registration-replaced.json", L"activation-committed.json"};
inline constexpr std::wstring_view pending = L"record.pending";
inline constexpr std::wstring_view lock_name = L"maintenance.lock";
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
    LARGE_INTEGER size{};
    if (config_path_detail::ObserveFileShape(file_, path) !=
        ConfigPathObservation::kObservedOnly || !GetFileSizeEx(file_, &size) ||
        size.QuadPart != 0 || !policy(file_, false) ||
        ValidateDirectoryHandle(parent, root) != FilesystemObservation::kObservedOnly ||
        !policy(parent, true)) return R::kUnsafe;
    return R::kHistoryOnly; // acquisition only; does not authenticate history
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
template <class Policy>
JournalStorageObservation ReadHistory(const std::wstring& root, HANDLE parent,
    CutoverJournalRecord* output, Policy& policy) {
  using R = JournalStorageObservation;
  if (ValidateDirectoryHandle(parent, root) != FilesystemObservation::kObservedOnly ||
      !policy(parent, true)) return R::kUnsafe;
  const auto pending_state = Exists(Path(root, pending));
  if (pending_state == Presence::kPresent) return R::kPending;
  if (pending_state != Presence::kAbsent) return R::kUnavailable;
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
  if (Exists(Path(root, pending)) != Presence::kAbsent) return R::kPending;
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
inline bool NativePolicy(HANDLE handle, bool directory) {
  return ObservePayloadObjectPolicy(handle, directory) == PayloadObjectObservation::kObservedOnly;
}
}  // namespace journal_storage_detail

template <class Policy>
JournalStorageObservation ObserveSyntheticJournalHistoryWith(
    std::wstring_view expected, std::wstring_view selected,
    CutoverJournalRecord* output, Policy policy) {
  using namespace journal_storage_detail;
  using R = JournalStorageObservation;
  if (!output || !FixtureRoot(expected, selected)) return R::kRejected;
  const std::wstring root(selected);
  DirectoryHandleRetention retained;
  if (retained.Observe(expected, selected, false, false) != FilesystemObservation::kObservedOnly)
    return R::kUnsafe;
  config_path_detail::Handle parent(CreateFileW(root.c_str(), READ_CONTROL | FILE_READ_ATTRIBUTES |
      FILE_LIST_DIRECTORY, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING,
      FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
  if (!parent.valid()) return R::kUnavailable;
  OperationLease lease;
  const auto acquired = lease.Acquire(root, parent.get(), policy);
  if (acquired != R::kHistoryOnly) return acquired;
  return ReadHistory(root, parent.get(), output, policy);
}
template <class Policy>
JournalStorageObservation PublishSyntheticJournalRecordWith(
    std::wstring_view expected, std::wstring_view selected,
    const CutoverJournalRecord& next, Policy policy) {
  using namespace journal_storage_detail;
  using R = JournalStorageObservation;
  if (!FixtureRoot(expected, selected)) return R::kRejected;
  std::string bytes;
  if (EncodeSyntheticCutoverJournal(next, &bytes) != CutoverJournalResult::kRecordOnly)
    return R::kRejected;
  const int slot = Slot(next.state);
  if (slot < 0) return R::kRejected;
  const std::wstring root(selected);
  DirectoryHandleRetention retained;
  if (retained.Observe(expected, selected, false, false) != FilesystemObservation::kObservedOnly)
    return R::kUnsafe;
  config_path_detail::Handle parent(CreateFileW(root.c_str(), READ_CONTROL | FILE_READ_ATTRIBUTES |
      FILE_LIST_DIRECTORY, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING,
      FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
  if (!parent.valid()) return R::kUnavailable;
  OperationLease lease;
  const auto acquired = lease.Acquire(root, parent.get(), policy);
  if (acquired != R::kHistoryOnly) return acquired;
  CutoverJournalRecord previous;
  const auto history = ReadHistory(root, parent.get(), &previous, policy);
  if (history != R::kEmpty && history != R::kHistoryOnly) return history;
  if ((history == R::kEmpty && slot != 0) ||
      (history == R::kHistoryOnly && !IsSyntheticCutoverJournalSuccessor(previous, next)))
    return R::kConflict;
  const auto temporary = Path(root, pending);
  const auto destination = Path(root, names[static_cast<std::size_t>(slot)]);
  // CREATE_NEW + no-overwrite handle rename. Leave failed pending artifacts
  // for review; never delete or truncate a pre-existing record.
  config_path_detail::Handle file(CreateFileW(temporary.c_str(),
      GENERIC_READ | GENERIC_WRITE | DELETE, 0, nullptr, CREATE_NEW,
      FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_WRITE_THROUGH, nullptr));
  if (!file.valid()) return R::kConflict;
  if (config_path_detail::ObserveFileShape(file.get(), temporary) !=
      ConfigPathObservation::kObservedOnly || !policy(file.get(), false) ||
      !policy(parent.get(), true)) return R::kUnsafe;
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
          ConfigPathObservation::kObservedOnly || !policy(file.get(), false) ||
      ValidateDirectoryHandle(parent.get(), root) != FilesystemObservation::kObservedOnly ||
      !policy(parent.get(), true)) return R::kPending;
  if (!RenameNoReplace(file.get(), destination)) return R::kPending;
  if (!FlushFileBuffers(file.get()) ||
      config_path_detail::ObserveFileShape(file.get(), destination) !=
          ConfigPathObservation::kObservedOnly || !policy(file.get(), false) ||
      !policy(parent.get(), true)) return R::kUnavailable; // publication ambiguous
  return R::kPublishedOnly;
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
