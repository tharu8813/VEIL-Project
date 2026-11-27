// SPDX-License-Identifier: MPL-2.0
#include "product_module_policy.h"
#include <tlhelp32.h>
#include <psapi.h>

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

bool ObserveImageBacking(HMODULE module, const fs::path& expected, bool require_single_link) {
  if (!module || !DriveAbsolute(expected)) return false;
  MEMORY_BASIC_INFORMATION region{};
  if (VirtualQuery(module, &region, sizeof(region)) != sizeof(region) ||
      region.Type != MEM_IMAGE || region.AllocationBase != module) return false;
  wchar_t loader_path[32768]{}, mapped[32768]{};
  const DWORD loader_length = GetModuleFileNameW(module, loader_path, 32768);
  if (!loader_length || loader_length >= 32768 ||
      !Same(loader_path, expected.native())) return false;
  const DWORD mapped_length = GetMappedFileNameW(GetCurrentProcess(), module, mapped, 32768);
  if (!mapped_length || mapped_length >= 32767) return false;

  // Reject redirected path components, then obtain both normalized DOS
  // and native device names from the same opened file handle.
  for (auto current = expected; !current.empty();) {
    const DWORD attrs = GetFileAttributesW(current.c_str());
    if (attrs == INVALID_FILE_ATTRIBUTES || (attrs & FILE_ATTRIBUTE_REPARSE_POINT))
      return false;
    const auto parent = current.parent_path();
    if (parent == current) break;
    current = parent;
  }
  Snapshot file(CreateFileW(expected.c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ,
      nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
  if (file.get() == INVALID_HANDLE_VALUE) return false;
  BY_HANDLE_FILE_INFORMATION info{};
  if (!GetFileInformationByHandle(file.get(), &info) ||
      (info.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) ||
      (require_single_link && info.nNumberOfLinks != 1)) return false;
  wchar_t dos[32768]{}, device[32768]{};
  const DWORD dos_length = GetFinalPathNameByHandleW(file.get(), dos, 32768,
      FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
  const DWORD device_length = GetFinalPathNameByHandleW(file.get(), device, 32768,
      FILE_NAME_NORMALIZED | VOLUME_NAME_NT);
  if (!dos_length || dos_length >= 32768 || !device_length || device_length >= 32768 ||
      !Same(dos, L"\\\\?\\" + expected.native()) ||
      !Same(std::wstring(mapped, mapped_length), std::wstring(device, device_length)))
    return false;
  // Reobserve the allocation and kernel-reported name before releasing the
  // disk handle. This still cannot detect private modified image pages or
  // an older section backed by a previously replaced same-name file.
  MEMORY_BASIC_INFORMATION after{};
  wchar_t mapped_after[32768]{};
  const DWORD after_length = GetMappedFileNameW(GetCurrentProcess(), module, mapped_after, 32768);
  return VirtualQuery(module, &after, sizeof(after)) == sizeof(after) &&
      after.Type == MEM_IMAGE && after.AllocationBase == module &&
      after_length == mapped_length &&
      Same(std::wstring(mapped_after, after_length), std::wstring(mapped, mapped_length));
}

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
  bool saw_host = false, saw_product = false;
  do {
    // Toolhelp's path field is bounded by MAX_PATH; refuse truncation.
    const auto length = wcsnlen_s(entry.szExePath, _countof(entry.szExePath));
    if (length == 0 || length >= _countof(entry.szExePath) - 1 ||
        !AllowedModulePath(fs::path(entry.szExePath), application, system))
      return false;
    const fs::path module_path(entry.szExePath);
    const bool local = Same(module_path.parent_path().native(), application.native());
    if (!ObserveImageBacking(entry.hModule, module_path, local)) return false;
    if (Same(entry.szExePath, executable)) saw_host = true;
    if (local && Same(module_path.filename().native(), L"veil-cef-product.dll")) saw_product = true;
    entry.dwSize = sizeof(entry);
  } while (Module32NextW(snapshot.get(), &entry));
  return GetLastError() == ERROR_NO_MORE_FILES && saw_host && saw_product;
}
}  // namespace veil_cef
