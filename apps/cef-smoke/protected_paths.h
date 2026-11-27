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
                        const std::wstring& volume) {
  if (!DriveAbsolute(path)) return false;
  for (auto current = path; !current.empty();) {
    const DWORD attrs = GetFileAttributesW(current.c_str());
    if (attrs == INVALID_FILE_ATTRIBUTES || (attrs & FILE_ATTRIBUTE_REPARSE_POINT))
      return false;
    const auto parent = current.parent_path();
    if (parent == current) break;
    current = parent;
  }
  const HANDLE h = CreateFileW(path.c_str(), 0,
      FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
      FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
  if (h == INVALID_HANDLE_VALUE) return false;
  BY_HANDLE_FILE_INFORMATION info{};
  wchar_t final_path[32768]{};
  const DWORD length = GetFinalPathNameByHandleW(h, final_path, 32768, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
  const bool valid = GetFileInformationByHandle(h, &info) &&
      !(info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) &&
      ((info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0) == directory &&
      (directory || info.nNumberOfLinks == 1) &&
      length > 4 && length < 32768 &&
      Same(final_path, L"\\\\?\\" + path.native());
  CloseHandle(h);
  if (!valid) return false;
  wchar_t mount[32768]{}, guid[128]{};
  return GetVolumePathNameW(path.c_str(), mount, 32768) &&
      GetDriveTypeW(mount) == DRIVE_FIXED &&
      GetVolumeNameForVolumeMountPointW(mount, guid, 128) && Same(guid, volume);
}

// The future caller must provide a separately authenticated registered product
// root and volume GUID. Neither is trusted merely because an env variable says
// so. This verifies path layout/identity only, not BitLocker or session authority.
inline bool VerifyPaths(const fs::path& product_root, const std::wstring& volume,
                        const Paths& supplied) {
  if (!DriveAbsolute(product_root) || volume.empty()) return false;
  const auto root = product_root / L"cef";
  const auto logs = root / L"logs";
  if (!Same(supplied.root.native(), root.native()) ||
      !Same(supplied.cache.native(), (root / L"cache").native()) ||
      !Same(supplied.scratch.native(), (root / L"temp").native()) ||
      !Same(supplied.log.native(), (logs / L"cef.log").native())) return false;
  for (const auto& dir : {product_root, supplied.root, supplied.cache, supplied.scratch, logs})
    if (!VerifyEntry(dir, true, volume)) return false;
  const DWORD log_attrs = GetFileAttributesW(supplied.log.c_str());
  if (log_attrs == INVALID_FILE_ATTRIBUTES)
    return GetLastError() == ERROR_FILE_NOT_FOUND;
  return VerifyEntry(supplied.log, false, volume);
}
}  // namespace veil_cef
