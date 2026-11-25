// SPDX-License-Identifier: MPL-2.0
// Development-only path/ACL observation. Never opens configuration content.
#ifndef VEIL_CHROMIUM_DEV_CONFIG_PATH_WIN_H_
#define VEIL_CHROMIUM_DEV_CONFIG_PATH_WIN_H_
#include "config-acl-win.h"
#include "profile-filesystem-win.h"

namespace veil::chromium_dev {
enum class ConfigPathObservation {
  kLexicalRejected, kUnavailable, kUnsafePath, kMultipleLinks,
  kAclRejected, kObservedOnly,
};

namespace config_path_detail {
class Handle {
 public:
  explicit Handle(HANDLE value) noexcept : value_(value) {}
  ~Handle() { if (valid()) CloseHandle(value_); }
  Handle(const Handle&) = delete;
  Handle& operator=(const Handle&) = delete;
  bool valid() const noexcept { return value_ && value_ != INVALID_HANDLE_VALUE; }
  HANDLE get() const noexcept { return value_; }
 private:
  HANDLE value_;
};
inline ConfigPathObservation ObserveFileShape(HANDLE file,
                                             std::wstring_view expected) noexcept {
  if (!file || file == INVALID_HANDLE_VALUE || GetFileType(file) != FILE_TYPE_DISK)
    return ConfigPathObservation::kUnavailable;
  BY_HANDLE_FILE_INFORMATION info{};
  if (!GetFileInformationByHandle(file, &info))
    return ConfigPathObservation::kUnavailable;
  if ((info.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) != 0)
    return ConfigPathObservation::kUnsafePath;
  if (info.nNumberOfLinks != 1) return ConfigPathObservation::kMultipleLinks;
  std::array<wchar_t, 1024> buffer{};
  const DWORD count = GetFinalPathNameByHandleW(file, buffer.data(),
      static_cast<DWORD>(buffer.size()), FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
  if (!count || count >= static_cast<DWORD>(buffer.size()))
    return ConfigPathObservation::kUnavailable;
  const std::wstring_view actual(buffer.data(), count);
  constexpr std::wstring_view prefix = L"\\\\?\\";
  if (actual.substr(0, prefix.size()) != prefix ||
      actual.substr(prefix.size()) != expected) return ConfigPathObservation::kUnsafePath;
  return ConfigPathObservation::kObservedOnly;
}
}  // namespace config_path_detail

// Strict synthetic scope: caller must independently establish a trusted root.
// Check all ancestors for path redirection; ACL-check the immediate config
// directory and file on opened handles. Higher ancestors' ACLs are not proven.
template <class AclObserver>
ConfigPathObservation ObserveConfigPathWith(
    std::wstring_view expected_directory, std::wstring_view selected_directory,
    std::wstring_view filename, AclObserver acl) {
  using namespace config_path_detail;
  if (Evaluate(expected_directory, selected_directory, false, false) !=
          PathDecision::kLexicalMatchOnly ||
      filename != L"client-fixture.json")
    return ConfigPathObservation::kLexicalRejected;
  const std::wstring directory(selected_directory);
  const std::wstring file_path = directory + L"\\" + std::wstring(filename);
  if (!SafeDedicatedPath(file_path)) return ConfigPathObservation::kLexicalRejected;
  DirectoryHandleRetention retained;
  if (retained.Observe(expected_directory, selected_directory, false, false) !=
      FilesystemObservation::kObservedOnly) return ConfigPathObservation::kUnsafePath;
  Handle parent(CreateFileW(directory.c_str(), READ_CONTROL | FILE_READ_ATTRIBUTES,
      FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING,
      FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
  if (!parent.valid()) return ConfigPathObservation::kUnavailable;
  if (ValidateDirectoryHandle(parent.get(), directory) !=
      FilesystemObservation::kObservedOnly) return ConfigPathObservation::kUnsafePath;
  if (acl(parent.get(), true) != ConfigAclObservation::kObservedOnly)
    return ConfigPathObservation::kAclRejected;
  // No FILE_READ_DATA; deny write/delete sharing for this file observation.
  Handle file(CreateFileW(file_path.c_str(), READ_CONTROL | FILE_READ_ATTRIBUTES,
      FILE_SHARE_READ, nullptr, OPEN_EXISTING,
      FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
  if (!file.valid()) return ConfigPathObservation::kUnavailable;
  auto shape = ObserveFileShape(file.get(), file_path);
  if (shape != ConfigPathObservation::kObservedOnly) return shape;
  if (acl(file.get(), false) != ConfigAclObservation::kObservedOnly ||
      acl(parent.get(), true) != ConfigAclObservation::kObservedOnly)
    return ConfigPathObservation::kAclRejected;
  if (ValidateDirectoryHandle(parent.get(), directory) !=
      FilesystemObservation::kObservedOnly) return ConfigPathObservation::kUnsafePath;
  return ObserveFileShape(file.get(), file_path);
  // Handles release before return. No content, config authority or access permit.
}

inline ConfigPathObservation ObserveSyntheticConfigPath(
    std::wstring_view expected, std::wstring_view selected,
    std::wstring_view filename) {
  return ObserveConfigPathWith(expected, selected, filename, ObserveConfigObjectHandle);
}
}  // namespace veil::chromium_dev
#endif
