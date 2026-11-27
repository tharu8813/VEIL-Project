// SPDX-License-Identifier: MPL-2.0
#include "protected_paths.h"
#include <iostream>
#include <fstream>

int main() {
  namespace fs = std::filesystem;
  using namespace veil_cef;
  std::error_code error;
  const auto temp = fs::temp_directory_path(error);
  if (error) return 1;
  const auto product = temp / (L"veil-cef-path-test-" + std::to_wstring(GetCurrentProcessId()));
  if (!fs::create_directory(product, error) || error) return 2;
  struct Cleanup {
    fs::path root;
    ~Cleanup() { std::error_code e; fs::remove_all(root, e); }
  } cleanup{product};
  Paths paths{product / L"cef", product / L"cef/cache", product / L"cef/temp",
              product / L"cef/logs/cef.log"};
  // Construct using individual components to keep native path separators.
  paths.cache = paths.root / L"cache";
  paths.scratch = paths.root / L"temp";
  paths.log = paths.root / L"logs" / L"cef.log";
  for (const auto& dir : {paths.root, paths.cache, paths.scratch, paths.log.parent_path()})
    if (!fs::create_directory(dir, error) || error) return 3;
  wchar_t mount[32768]{}, volume[128]{};
  if (!GetVolumePathNameW(product.c_str(), mount, 32768) ||
      !GetVolumeNameForVolumeMountPointW(mount, volume, 128)) return 4;
  if (!VerifyPaths(product, volume, paths)) return 5;
  if (VerifyPaths(product, L"wrong-volume", paths)) return 6;
  auto bad = paths;
  bad.cache = product;
  if (VerifyPaths(product, volume, bad)) return 7;
  bad = paths;
  bad.scratch = paths.root / L"cache" / L".." / L"temp";
  if (VerifyPaths(product, volume, bad)) return 8;
  if (!fs::create_directory(paths.log, error) || error) return 9;
  if (VerifyPaths(product, volume, paths)) return 10;
  if (!fs::remove(paths.log, error) || error) return 11;
  { std::ofstream out(paths.log); out << "synthetic"; if (!out) return 12; }
  if (!VerifyPaths(product, volume, paths)) return 13;
  const auto alias = paths.root / L"logs" / L"alias.log";
  if (!CreateHardLinkW(alias.c_str(), paths.log.c_str(), nullptr)) return 14;
  if (VerifyPaths(product, volume, paths)) return 15;
  if (!fs::remove(alias, error) || error || !fs::remove(paths.log, error) || error) return 16;
  if (!fs::remove(paths.cache, error) || error || VerifyPaths(product, volume, paths)) return 17;
  for (const auto* key : {L"VEIL_CEF_PROTECTED_ROOT", L"VEIL_CEF_PROTECTED_CACHE",
                          L"VEIL_CEF_PROTECTED_LOG_FILE"})
    if (!SetEnvironmentVariableW(key, nullptr) &&
        GetLastError() != ERROR_ENVVAR_NOT_FOUND) return 18;
  if (HasProductEnvironment()) return 19;
  for (const auto* key : {L"VEIL_CEF_PROTECTED_ROOT", L"VEIL_CEF_PROTECTED_CACHE",
                          L"VEIL_CEF_PROTECTED_LOG_FILE"}) {
    if (!SetEnvironmentVariableW(key, L"synthetic") ||
        !HasProductEnvironment() ||
        !SetEnvironmentVariableW(key, nullptr) ||
        HasProductEnvironment()) return 20;
  }
  std::cout << "PASS: synthetic CEF path layout, volume identity, obstruction, hardlink and environment refusal\n";
  return 0;
}
