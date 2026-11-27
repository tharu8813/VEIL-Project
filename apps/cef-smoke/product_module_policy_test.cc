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
  std::cout << "PASS: strict synthetic loaded module directory policy\n";
  return 0;
}
