// SPDX-License-Identifier: MPL-2.0
#pragma once
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <filesystem>
#include <string>

namespace veil_cef {
namespace fs = std::filesystem;

inline bool Same(const std::wstring& a, const std::wstring& b) {
  return CompareStringOrdinal(a.c_str(), -1, b.c_str(), -1, TRUE) == CSTR_EQUAL;
}

// These inputs are proposed product configuration, never authorization.
// Empty variables and environment lookup errors must not select a dev fallback.
inline bool HasProductEnvironment() {
  for (const auto* key : {L"VEIL_CEF_PROTECTED_ROOT", L"VEIL_CEF_PROTECTED_CACHE",
                          L"VEIL_CEF_PROTECTED_LOG_FILE"}) {
    SetLastError(ERROR_SUCCESS);
    wchar_t value[2]{};
    if (GetEnvironmentVariableW(key, value, 2) != 0 ||
        GetLastError() != ERROR_ENVVAR_NOT_FOUND) return true;
  }
  return false;
}

struct Paths {
  fs::path root, cache, scratch, log;
};

// Only local drive-absolute canonical paths are eligible. The handle checks
// below reject aliases, including reparse ancestors and short-name spellings.
inline bool DriveAbsolute(const fs::path& path) {
  const auto& s = path.native();
  return s.size() >= 3 &&
      ((s[0] >= L'A' && s[0] <= L'Z') || (s[0] >= L'a' && s[0] <= L'z')) &&
      s[1] == L':' && s[2] == L'\\' &&
      s.find(L':', 2) == std::wstring::npos &&
      s.find(L'/') == std::wstring::npos &&
      s.find(L'\0') == std::wstring::npos &&
      path.lexically_normal() == path;
}

inline bool VerifyEntry(const fs::path& path, bool directory,
                        const std::wstring& volume, const char** failure = nullptr) {
  const auto reject = [failure](const char* reason) {
    if (failure) *failure = reason;
    return false;
  };
  if (!DriveAbsolute(path)) return reject("non-canonical-drive-path");
  for (auto current = path; !current.empty();) {
    const DWORD attrs = GetFileAttributesW(current.c_str());
    if (attrs == INVALID_FILE_ATTRIBUTES) return reject("ancestor-attributes");
    if (attrs & FILE_ATTRIBUTE_REPARSE_POINT) return reject("ancestor-reparse");
    const auto parent = current.parent_path();
    if (parent == current) break;
    current = parent;
  }
  const HANDLE h = CreateFileW(path.c_str(), 0,
      FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
      FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
  if (h == INVALID_HANDLE_VALUE) return reject("open-entry");
  BY_HANDLE_FILE_INFORMATION info{};
  wchar_t final_path[32768]{};
  const DWORD length = GetFinalPathNameByHandleW(h, final_path, 32768, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
  const bool have_info = GetFileInformationByHandle(h, &info) != 0;
  CloseHandle(h);
  if (!have_info) return reject("handle-info");
  if (info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) return reject("handle-reparse");
  if (((info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0) != directory)
    return reject("entry-type");
  if (!directory && info.nNumberOfLinks != 1) return reject("file-links");
  if (length <= 4 || length >= 32768) return reject("final-path-query");
  if (!Same(final_path, L"\\\\?\\" + path.native())) return reject("final-path-mismatch");
  wchar_t mount[32768]{}, guid[128]{};
  if (!GetVolumePathNameW(path.c_str(), mount, 32768)) return reject("mount-query");
  if (GetDriveTypeW(mount) != DRIVE_FIXED) return reject("drive-type");
  if (!GetVolumeNameForVolumeMountPointW(mount, guid, 128)) return reject("volume-query");
  if (!Same(guid, volume)) return reject("volume-mismatch");
  return true;
}

// The future caller must provide a separately authenticated registered product
// root and volume GUID. Neither is trusted merely because an env variable says
// so. This verifies path layout/identity only, not BitLocker or session authority.
inline bool VerifyPaths(const fs::path& product_root, const std::wstring& volume,
                        const Paths& supplied, const char** failure = nullptr) {
  const auto reject = [failure](const char* reason) {
    if (failure) *failure = reason;
    return false;
  };
  if (!DriveAbsolute(product_root) || volume.empty()) return reject("root-or-guid");
  const auto root = product_root / L"cef";
  const auto logs = root / L"logs";
  if (!Same(supplied.root.native(), root.native()) ||
      !Same(supplied.cache.native(), (root / L"cache").native()) ||
      !Same(supplied.scratch.native(), (root / L"temp").native()) ||
      !Same(supplied.log.native(), (logs / L"cef.log").native())) return reject("layout");
  for (const auto& dir : {product_root, supplied.root, supplied.cache, supplied.scratch, logs})
    if (!VerifyEntry(dir, true, volume, failure)) return false;
  const DWORD log_attrs = GetFileAttributesW(supplied.log.c_str());
  if (log_attrs == INVALID_FILE_ATTRIBUTES)
    return GetLastError() == ERROR_FILE_NOT_FOUND ? true : reject("log-attributes");
  return VerifyEntry(supplied.log, false, volume, failure);
}
}  // namespace veil_cef
