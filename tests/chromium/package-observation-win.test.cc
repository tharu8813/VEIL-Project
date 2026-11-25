// SPDX-License-Identifier: MPL-2.0
#include "../../chromium/dev/package-observation-win.h"
#include <fstream>
#include <filesystem>
#include <iostream>
using namespace veil::chromium_dev;
bool ReadFixture(const std::filesystem::path& path, std::string* bytes) {
  std::ifstream stream(path, std::ios::binary);
  if (!stream) return false;
  char c;
  while (stream.get(c)) {
    if (bytes->size() == kManifestFixtureLimit) return false;
    bytes->push_back(c);
  }
  return stream.eof() && !stream.bad() && !bytes->empty();
}
int wmain(int argc, wchar_t** argv) {
  if (argc != 3) return 1;
  const std::wstring root(argv[1]);
  std::string pin;
  for (const auto c : std::wstring_view(argv[2])) {
    if (!((c >= L'0' && c <= L'9') || (c >= L'a' && c <= L'f') ||
          (c >= L'A' && c <= L'F'))) return 2;
    pin.push_back(static_cast<char>(c));
  }
  if (!ValidClientHash(pin)) return 2;
  const std::filesystem::path base(root);
  std::string manifest, cms;
  if (!ReadFixture(base / L"manifest-fixture.json", &manifest) ||
      !ReadFixture(base / L"manifest-fixture.p7s", &cms)) return 3;
  const auto payload = root + L"\\Payload";
  const auto resource = payload + L"\\Broker\\resource.bin";
  const auto extra = payload + L"\\Broker\\extra.bin";
  ManifestInventory sentinel{{"old", "old", "old", 999}, {{"old", "old", 999}}};
  auto out = sentinel;
  auto unchanged = [&] {
    return out.client.source_commit == "old" && out.client.version == "old" &&
        out.client.sha256 == "old" && out.client.bytes == 999 &&
        out.files.size() == 1 && out.files[0].path == "old" &&
        out.files[0].sha256 == "old" && out.files[0].bytes == 999;
  };
  auto write = [](const std::wstring& path, std::string_view bytes) {
    config_path_detail::Handle file(CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr,
        CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr));
    DWORD count = 0;
    return file.valid() && WriteFile(file.get(), bytes.data(), static_cast<DWORD>(bytes.size()),
        &count, nullptr) && count == static_cast<DWORD>(bytes.size());
  };
  unsigned calls = 0;
  auto injected = [&](HANDLE file, const std::wstring& path, std::string_view signer_pin) {
    ++calls;
    if (path != payload + L"\\Chromium\\client-fixture.exe" || !SameClientHash(pin, signer_pin))
      return ClientSignatureObservation::kNotTrusted;
    LARGE_INTEGER size{};
    if (!GetFileSizeEx(file, &size) || size.QuadPart <= 0)
      return ClientSignatureObservation::kUnavailable;
    return ClientSignatureObservation::kPolicyMatchOnly;
  };
  using Result = PackageObservation;
  auto observe = [&] {
    return ObserveSyntheticPackageWith(payload, payload, manifest, cms, pin, &out,
        ObserveSyntheticManifestSignature, injected);
  };
  if (ObserveSyntheticPackageOfflinePolicy(payload, payload, manifest, cms, pin, &out) !=
      Result::kManifestRejected || !unchanged()) return 4;
  if (ObserveSyntheticPackageWith(payload, payload, manifest + " ", cms, pin, &out,
      ObserveSyntheticManifestSignature, injected) != Result::kManifestRejected ||
      calls || !unchanged()) return 5;
  if (!DeleteFileW(resource.c_str()) || observe() != Result::kInventoryMismatch ||
      calls || !unchanged() || !write(resource, "abc")) return 6;
  if (!write(extra, "abc") || observe() != Result::kInventoryMismatch ||
      calls || !unchanged() || !DeleteFileW(extra.c_str())) return 7;
  if (!write(resource, "abd") || observe() != Result::kInventoryMismatch ||
      calls || !unchanged() || !write(resource, "abc")) return 8;
  // Actual unsigned client must fail after genuine CMS and inventory checks.
  if (ObserveSyntheticPackageWith(payload, payload, manifest, cms, pin, &out,
      ObserveSyntheticManifestSignature, ObserveOfflinePinnedClientHandle) !=
      Result::kClientRejected || !unchanged()) return 9;
  // Model a persistent change to a non-client file while client is checked.
  auto mutate = [&](HANDLE file, const std::wstring& path, std::string_view signer_pin) {
    if (injected(file, path, signer_pin) != ClientSignatureObservation::kPolicyMatchOnly ||
        !write(resource, "abd")) return ClientSignatureObservation::kNotTrusted;
    return ClientSignatureObservation::kPolicyMatchOnly;
  };
  if (ObserveSyntheticPackageWith(payload, payload, manifest, cms, pin, &out,
      ObserveSyntheticManifestSignature, mutate) != Result::kRescanRejected ||
      calls != 1 || !unchanged() || !write(resource, "abc")) return 10;
  if (observe() != Result::kObservationsOnly || calls != 2 || out.files.size() != 2 ||
      out.client.version != "1.1.0-alpha.1" || !ValidClientHash(out.client.sha256)) return 11;
  std::cout << "PASS: native signed inventory and payload scan, missing/extra/tampered early refusal, unsigned refusal, persistent mutation rescan refusal, injected positive only; no package trust\n";
}
