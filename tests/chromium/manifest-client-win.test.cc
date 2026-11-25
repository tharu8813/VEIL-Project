// SPDX-License-Identifier: MPL-2.0
#include "../../chromium/dev/manifest-client-policy-win.h"
#include <filesystem>
#include <fstream>
#include <iostream>
using namespace veil::chromium_dev;
bool ReadFixture(const std::filesystem::path& path, std::string* bytes) {
  std::ifstream file(path, std::ios::binary);
  if (!file) return false;
  char c;
  while (file.get(c)) {
    if (bytes->size() == kManifestFixtureLimit) return false;
    bytes->push_back(c);
  }
  return file.eof() && !file.bad() && !bytes->empty();
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
  std::string manifest, cms, wrong_size, size_cms, wrong_hash, hash_cms;
  if (!ReadFixture(base / L"manifest-fixture.json", &manifest) ||
      !ReadFixture(base / L"manifest-fixture.p7s", &cms) ||
      !ReadFixture(base / L"size-fixture.json", &wrong_size) ||
      !ReadFixture(base / L"size-fixture.p7s", &size_cms) ||
      !ReadFixture(base / L"hash-fixture.json", &wrong_hash) ||
      !ReadFixture(base / L"hash-fixture.p7s", &hash_cms)) return 3;
  ManifestClientEntry output{"old", "old", "old", 999};
  const auto unchanged = [&] {
    return output.source_commit == "old" && output.version == "old" &&
        output.sha256 == "old" && output.bytes == 999;
  };
  unsigned calls = 0;
  auto injected = [&](HANDLE file, const std::wstring& path, std::string_view signer_pin) {
    ++calls;
    if (path != root + L"\\Chromium\\client-fixture.exe" ||
        !SameClientHash(signer_pin, pin)) return ClientSignatureObservation::kNotTrusted;
    LARGE_INTEGER size{};
    if (!GetFileSizeEx(file, &size) || size.QuadPart <= 0)
      return ClientSignatureObservation::kUnavailable;
    return ClientSignatureObservation::kPolicyMatchOnly; // synthetic only
  };
  if (ObserveSyntheticManifestClientWith(root, root, manifest + " ", cms, pin, &output, injected) !=
      ManifestClientObservation::kManifestRejected || calls || !unchanged()) return 4;
  auto wrong_pin = pin;
  wrong_pin[0] = (wrong_pin[0] == 'a' || wrong_pin[0] == 'A') ? 'b' : 'a';
  if (ObserveSyntheticManifestClientWith(root, root, manifest, cms, wrong_pin, &output, injected) !=
      ManifestClientObservation::kManifestRejected || calls || !unchanged()) return 5;
  if (ObserveSyntheticManifestClientWith(root, root, wrong_size, size_cms, pin, &output, injected) !=
      ManifestClientObservation::kSizeMismatch || calls || !unchanged()) return 6;
  if (ObserveSyntheticManifestClientWith(root, root, wrong_hash, hash_cms, pin, &output, injected) !=
      ManifestClientObservation::kHashMismatch || calls || !unchanged()) return 7;
  // Real unsigned fixture is rejected, despite valid signed manifest and hash.
  if (ObserveSyntheticManifestClient(root, root, manifest, cms, pin, &output) !=
      ManifestClientObservation::kSignatureRejected || !unchanged()) return 8;
  if (ObserveSyntheticManifestClientWith(root, root, manifest, cms, pin, &output, injected) !=
      ManifestClientObservation::kObservationsOnly || calls != 1 ||
      output.version != "1.1.0-alpha.1" || output.source_commit != std::string(40, 'b') ||
      !ValidClientHash(output.sha256) || !output.bytes) return 9;
  output = {"old", "old", "old", 999};
  const auto alias = root + L"\\Chromium\\alias.exe";
  const auto client = root + L"\\Chromium\\client-fixture.exe";
  if (!CreateHardLinkW(alias.c_str(), client.c_str(), nullptr)) return 10;
  const auto linked = ObserveSyntheticManifestClientWith(root, root, manifest, cms, pin, &output, injected);
  if (!DeleteFileW(alias.c_str()) || linked != ManifestClientObservation::kClientRejected ||
      calls != 1 || !unchanged()) return 11;
  if (ObserveSyntheticManifestClientWith(root, root + L"\\other", manifest, cms, pin, &output, injected) !=
      ManifestClientObservation::kRejected || calls != 1 || !unchanged()) return 12;
  // The genuine signed but untrusted CMS fixture cannot reach file inspection.
  if (ObserveSyntheticManifestClientOfflinePolicy(root, root, manifest, cms, pin, &output) !=
      ManifestClientObservation::kManifestRejected || !unchanged()) return 13;
  if (ObserveSyntheticManifestClientWithPolicies(root, root, manifest, cms, pin, &output,
      ObserveSyntheticOfflineSignedEntry, injected) !=
      ManifestClientObservation::kManifestRejected || calls != 1 || !unchanged()) return 14;
  unsigned entry_calls = 0;
  auto fixture_entry = [&](std::string_view m, std::string_view s, std::string_view p,
                           ManifestClientEntry* entry) {
    ++entry_calls;
    return ObserveSyntheticSignedEntry(m, s, p, entry);
  };
  // Inject only the weaker entry policy, not the native image policy.
  if (ObserveSyntheticManifestClientWithPolicies(root, root, manifest, cms, pin, &output,
      fixture_entry, ObserveOfflinePinnedClientHandle) !=
      ManifestClientObservation::kSignatureRejected || entry_calls != 1 ||
      !unchanged()) return 15;
  if (ObserveSyntheticManifestClientWithPolicies(root, root, wrong_size, size_cms, pin, &output,
      fixture_entry, injected) != ManifestClientObservation::kSizeMismatch ||
      entry_calls != 2 || calls != 1 || !unchanged()) return 16;
  if (ObserveSyntheticManifestClientWithPolicies(root, root, manifest, cms, pin, &output,
      fixture_entry, injected) != ManifestClientObservation::kObservationsOnly ||
      entry_calls != 3 || calls != 2 || !ValidClientHash(output.sha256)) return 17;
  std::cout << "PASS: strict manifest policy refuses untrusted CMS before client checks, native unsigned client refused, injected ordering only; trusted chain positive NOT RUN\n";
  std::cout << "PASS: native signed manifest binds exact fixture size/hash and same pin; unsigned refused, injected positive only, no package approval\n";
}
