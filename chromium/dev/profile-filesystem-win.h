// SPDX-License-Identifier: MPL-2.0
// Standalone Windows read-only observation for dedicated synthetic profiles.
// This is NOT a runtime guard, an atomic path proof, or launch authorization.
#ifndef VEIL_CHROMIUM_DEV_PROFILE_FILESYSTEM_WIN_H_
#define VEIL_CHROMIUM_DEV_PROFILE_FILESYSTEM_WIN_H_
#include "profile-path-contract.h"
#include <windows.h>
#include <array>
#include <string>

namespace veil::chromium_dev {
enum class FilesystemObservation {
  kLexicalRejected,
  kNonFixedDrive,
  kUnavailable,
  kNotDirectory,
  kReparsePoint,
  kUnexpectedFinalPath,
  kObservedOnly,
};

inline FilesystemObservation ValidateDirectoryHandle(
    HANDLE handle, const std::wstring& path) noexcept {
  FILE_ATTRIBUTE_TAG_INFO info{};
  const BOOL attributes = GetFileInformationByHandleEx(
      handle, FileAttributeTagInfo, &info, sizeof(info));
  if (!attributes) {
    return FilesystemObservation::kUnavailable;
  }
  if ((info.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0) {
    return FilesystemObservation::kReparsePoint;
  }
  if ((info.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0) {
    return FilesystemObservation::kNotDirectory;
  }
  std::array<wchar_t, 1024> buffer{};
  const DWORD count = GetFinalPathNameByHandleW(
      handle, buffer.data(), static_cast<DWORD>(buffer.size()),
      FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
  if (count == 0 || count >= static_cast<DWORD>(buffer.size()))
    return FilesystemObservation::kUnavailable;
  // Compare without allocating while the caller owns an open raw HANDLE.
  constexpr std::wstring_view prefix = L"\\\\?\\";
  const std::wstring_view actual(buffer.data(), count);
  if (actual.size() < prefix.size() ||
      actual.substr(0, prefix.size()) != prefix ||
      actual.substr(prefix.size()) != std::wstring_view(path))
    return FilesystemObservation::kUnexpectedFinalPath;
  return FilesystemObservation::kObservedOnly;
}

inline FilesystemObservation ObserveDirectoryComponent(
    const std::wstring& path) {
  // OPEN_REPARSE_POINT inspects the final component itself; ancestor components
  // are checked separately. No directories, files or registry keys are created.
  HANDLE handle = CreateFileW(
      path.c_str(), FILE_READ_ATTRIBUTES,
      FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
      OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
      nullptr);
  if (handle == INVALID_HANDLE_VALUE)
    return FilesystemObservation::kUnavailable;
  const auto result = ValidateDirectoryHandle(handle, path);
  CloseHandle(handle);
  return result;
}

inline FilesystemObservation ObserveExistingDedicatedPath(
    std::wstring_view expected_root, std::wstring_view selected_path,
    bool policy_override_present, bool fallback_requested) {
  if (Evaluate(expected_root, selected_path, policy_override_present,
               fallback_requested) != PathDecision::kLexicalMatchOnly)
    return FilesystemObservation::kLexicalRejected;
  const std::wstring path(selected_path);
  const std::wstring drive = path.substr(0, 3);
  if (GetDriveTypeW(drive.c_str()) != DRIVE_FIXED)
    return FilesystemObservation::kNonFixedDrive;
  auto result = ObserveDirectoryComponent(drive);
  if (result != FilesystemObservation::kObservedOnly) return result;
  for (std::size_t end = path.find(L'\\', 3);;
       end = path.find(L'\\', end + 1)) {
    result = ObserveDirectoryComponent(path.substr(0, end));
    if (result != FilesystemObservation::kObservedOnly) return result;
    if (end == std::wstring::npos) break;
  }
  // Handles have been closed: another process can replace a path after this
  // observation. Runtime integration needs a separate atomic ownership design.
  return FilesystemObservation::kObservedOnly;
}
// Experimental retention of existing non-root directory handles. Holding handles
// without FILE_SHARE_DELETE can conflict with DELETE access; it does not prove
// immutable reparse metadata, trusted ownership, or atomic path traversal.
class DirectoryHandleRetention {
 public:
  DirectoryHandleRetention() = default;
  DirectoryHandleRetention(const DirectoryHandleRetention&) = delete;
  DirectoryHandleRetention& operator=(const DirectoryHandleRetention&) = delete;
  ~DirectoryHandleRetention() { Reset(); }

  // Read the final retained handle, rather than reopening a DOS path.
  // A zero or oversized result is unavailable; no handle escapes the owner.
  DWORD ReadFinalVolumePath(std::array<wchar_t, 1024>& buffer) const noexcept {
    if (count_ == 0) return 0;
    return GetFinalPathNameByHandleW(
        handles_[count_ - 1], buffer.data(), static_cast<DWORD>(buffer.size()),
        FILE_NAME_NORMALIZED | VOLUME_NAME_GUID);
  }

  void Reset() noexcept {
    while (count_ != 0) CloseHandle(handles_[--count_]);
  }

  FilesystemObservation Observe(std::wstring_view expected_root,
                                std::wstring_view selected_path,
                                bool policy_override_present,
                                bool fallback_requested) {
    Reset();
    if (Evaluate(expected_root, selected_path, policy_override_present,
                 fallback_requested) != PathDecision::kLexicalMatchOnly)
      return FilesystemObservation::kLexicalRejected;
    const std::wstring path(selected_path);
    const auto drive = path.substr(0, 3);
    if (GetDriveTypeW(drive.c_str()) != DRIVE_FIXED)
      return FilesystemObservation::kNonFixedDrive;
    auto result = ObserveDirectoryComponent(drive);
    if (result != FilesystemObservation::kObservedOnly) return result;
    try {
      for (std::size_t end = path.find(L'\\', 3);;
           end = path.find(L'\\', end + 1)) {
        const auto component = path.substr(0, end);
        if (count_ == handles_.size()) return Fail(FilesystemObservation::kUnavailable);
        HANDLE handle = CreateFileW(
            component.c_str(), FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES,
            FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
        if (handle == INVALID_HANDLE_VALUE) return Fail(FilesystemObservation::kUnavailable);
        handles_[count_++] = handle;
        result = ValidateDirectoryHandle(handle, component);
        if (result != FilesystemObservation::kObservedOnly) return Fail(result);
        if (end == std::wstring::npos) break;
      }
    } catch (...) {
      Reset();
      throw;
    }
    return FilesystemObservation::kObservedOnly;
  }

 private:
  FilesystemObservation Fail(FilesystemObservation result) noexcept {
    Reset();
    return result;
  }

  // Lexical contract limits path length below 260 characters.
  std::array<HANDLE, 130> handles_{};
  std::size_t count_ = 0;
};

}  // namespace veil::chromium_dev
#endif
