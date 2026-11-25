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
  FILE_ATTRIBUTE_TAG_INFO info{};
  const BOOL attributes = GetFileInformationByHandleEx(
      handle, FileAttributeTagInfo, &info, sizeof(info));
  if (!attributes) {
    CloseHandle(handle);
    return FilesystemObservation::kUnavailable;
  }
  if ((info.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0) {
    CloseHandle(handle);
    return FilesystemObservation::kReparsePoint;
  }
  if ((info.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0) {
    CloseHandle(handle);
    return FilesystemObservation::kNotDirectory;
  }
  std::array<wchar_t, 1024> buffer{};
  const DWORD count = GetFinalPathNameByHandleW(
      handle, buffer.data(), static_cast<DWORD>(buffer.size()),
      FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
  CloseHandle(handle);
  if (count == 0 || count >= static_cast<DWORD>(buffer.size()))
    return FilesystemObservation::kUnavailable;
  const std::wstring expected = L"\\\\?\\" + path;
  if (std::wstring_view(buffer.data(), count) != std::wstring_view(expected))
    return FilesystemObservation::kUnexpectedFinalPath;
  return FilesystemObservation::kObservedOnly;
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
}  // namespace veil::chromium_dev
#endif
