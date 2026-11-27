// SPDX-License-Identifier: MPL-2.0
#include "product_module_policy.h"
#include <tlhelp32.h>

namespace veil_cef {
namespace {
class Snapshot {
 public:
  explicit Snapshot(HANDLE handle) : handle_(handle) {}
  ~Snapshot() { if (handle_ != INVALID_HANDLE_VALUE) CloseHandle(handle_); }
  Snapshot(const Snapshot&) = delete;
  Snapshot& operator=(const Snapshot&) = delete;
  HANDLE get() const { return handle_; }
 private:
  HANDLE handle_;
};
}  // namespace

bool RestrictAndObserveLoadedModules() {
  // A necessary post-bootstrap restriction. The official bootstrap and DLL
  // imports may have run before this point; do not claim otherwise.
  if (!SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_APPLICATION_DIR |
                                LOAD_LIBRARY_SEARCH_SYSTEM32)) return false;
  PROCESS_MITIGATION_IMAGE_LOAD_POLICY mitigation{};
  if (!GetProcessMitigationPolicy(GetCurrentProcess(), ProcessImageLoadPolicy,
                                  &mitigation, sizeof(mitigation)) ||
      !mitigation.NoRemoteImages || !mitigation.NoLowMandatoryLabelImages ||
      !mitigation.PreferSystem32Images) return false;
  wchar_t executable[32768]{}, system_path[32768]{};
  const DWORD exe_length = GetModuleFileNameW(nullptr, executable, 32768);
  const UINT system_length = GetSystemDirectoryW(system_path, 32768);
  if (!exe_length || exe_length >= 32768 || !system_length || system_length >= 32768)
    return false;
  const fs::path application = fs::path(executable).parent_path();
  const fs::path system(system_path);
  Snapshot snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32,
                                            GetCurrentProcessId()));
  if (snapshot.get() == INVALID_HANDLE_VALUE) return false;
  MODULEENTRY32W entry{};
  entry.dwSize = sizeof(entry);
  if (!Module32FirstW(snapshot.get(), &entry)) return false;
  bool saw_host = false;
  do {
    // Toolhelp's path field is bounded by MAX_PATH; refuse truncation.
    const auto length = wcsnlen_s(entry.szExePath, _countof(entry.szExePath));
    if (length == 0 || length >= _countof(entry.szExePath) - 1 ||
        !AllowedModulePath(fs::path(entry.szExePath), application, system))
      return false;
    if (Same(entry.szExePath, executable)) saw_host = true;
    entry.dwSize = sizeof(entry);
  } while (Module32NextW(snapshot.get(), &entry));
  return GetLastError() == ERROR_NO_MORE_FILES && saw_host;
}
}  // namespace veil_cef
