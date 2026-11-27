// SPDX-License-Identifier: MPL-2.0
#include "product_module_policy.h"
#include <iostream>

int main() {
  using namespace veil_cef;
  const fs::path app(L"C:\\Program Files\\VEIL\\Cef");
  const fs::path sys(L"C:\\Windows\\System32");
  if (!AllowedModulePath(app / L"libcef.dll", app, sys) ||
      !AllowedModulePath(sys / L"kernel32.dll", app, sys) ||
      !AllowedModulePath(fs::path(L"c:\\windows\\system32\\ntdll.dll"), app, sys))
    return 1;
  for (const auto* path : {L"C:\\Users\\example\\evil.dll",
      L"C:\\Windows\\System32-other\\evil.dll", L"C:\\Program Files\\VEIL\\Cef\\sub\\evil.dll",
      L"C:\\Program Files\\VEIL\\Cef\\..\\evil.dll", L"relative.dll",
      L"\\\\server\\share\\evil.dll", L"C:\\Windows\\System32\\a.dll:stream"})
    if (AllowedModulePath(fs::path(path), app, sys)) return 2;
  // Actual Windows image mapping of this synthetic test executable.
  // No CEF or service is launched and no system file is modified.
  wchar_t self[32768]{};
  const DWORD self_length = GetModuleFileNameW(nullptr, self, 32768);
  HMODULE module = GetModuleHandleW(nullptr);
  if (!self_length || self_length >= 32768 ||
      !ObserveImageBacking(module, fs::path(self), true)) return 3;
  if (ObserveImageBacking(module, fs::path(self).parent_path() / L"not-this.exe", true))
    return 4;
  void* private_memory = VirtualAlloc(nullptr, 4096, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
  if (!private_memory) return 5;
  const bool private_accepted = ObserveImageBacking(
      reinterpret_cast<HMODULE>(private_memory), fs::path(self), true);
  VirtualFree(private_memory, 0, MEM_RELEASE);
  if (private_accepted) return 6;
  HANDLE file = CreateFileW(self, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
                            FILE_ATTRIBUTE_NORMAL, nullptr);
  if (file == INVALID_HANDLE_VALUE) return 7;
  HANDLE mapping = CreateFileMappingW(file, nullptr, PAGE_READONLY, 0, 0, nullptr);
  CloseHandle(file);
  if (!mapping) return 8;
  void* data = MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, 0);
  CloseHandle(mapping);
  if (!data) return 9;
  const bool data_accepted = ObserveImageBacking(reinterpret_cast<HMODULE>(data), fs::path(self), true);
  UnmapViewOfFile(data);
  if (data_accepted) return 10;
  std::cout << "PASS: native module directory and actual image mapping name checks\n";
  return 0;
}
