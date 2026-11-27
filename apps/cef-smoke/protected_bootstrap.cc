// SPDX-License-Identifier: MPL-2.0
#include "protected_bootstrap.h"

namespace veil_cef {
namespace {
bool ReadPath(const wchar_t* key, fs::path& path) {
  const DWORD length = GetEnvironmentVariableW(key, nullptr, 0);
  if (length < 2 || length > 32768) return false;
  std::wstring value(length, L'\0');
  const DWORD copied = GetEnvironmentVariableW(key, value.data(), length);
  if (copied != length - 1) return false;
  value.resize(copied);
  path = fs::path(value);
  return DriveAbsolute(path);
}
bool Absent(const wchar_t* key) {
  SetLastError(ERROR_SUCCESS);
  wchar_t buffer[2]{};
  return GetEnvironmentVariableW(key, buffer, 2) == 0 &&
      GetLastError() == ERROR_ENVVAR_NOT_FOUND;
}
}  // namespace

bool ReadBootstrapEnvironment(BootstrapEnvironment& supplied) {
  BootstrapEnvironment result;
  // The protected launcher must strip known fixture and logging overrides.
  // Unknown inherited variables still require an installer/launch audit.
  if (!Absent(L"CHROME_LOG_FILE") || !Absent(L"VEIL_CEF_WPF_TEST_URL") ||
      !Absent(L"VEIL_CEF_WPF_TABS") ||
      !ReadPath(L"VEIL_CEF_PROTECTED_ROOT", result.paths.root) ||
      !ReadPath(L"VEIL_CEF_PROTECTED_CACHE", result.paths.cache) ||
      !ReadPath(L"VEIL_CEF_PROTECTED_LOG_FILE", result.paths.log) ||
      !ReadPath(L"TEMP", result.temp) || !ReadPath(L"TMP", result.tmp) ||
      !Same(result.temp.native(), result.tmp.native())) return false;
  result.paths.scratch = result.temp;
  supplied = std::move(result);
  return true;
}

BootstrapCheck VerifyRegisteredBootstrap(const RegisteredBootstrap& registration) {
  return CheckBootstrap(registration, RequestChildVerification, ReadBootstrapEnvironment);
}
}  // namespace veil_cef
