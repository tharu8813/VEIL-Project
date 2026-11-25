// SPDX-License-Identifier: MPL-2.0
#include "../../chromium/dev/manifest-entry-win.h"
#include <fstream>
#include <filesystem>
#include <iostream>
using namespace veil::chromium_dev;

bool ReadFixture(const std::filesystem::path& path, std::string* bytes) {
  std::ifstream stream(path, std::ios::binary);
  if (!stream) return false;
  char c = 0;
  while (stream.get(c)) {
    if (bytes->size() == kManifestFixtureLimit) return false;
    bytes->push_back(c);
  }
  return stream.eof() && !stream.bad() && !bytes->empty();
}
int wmain(int argc, wchar_t** argv) {
  if (argc != 3) return 1;
  const std::filesystem::path root(argv[1]);
  std::string pin;
  for (const wchar_t c : std::wstring_view(argv[2])) {
    if (!((c >= L'0' && c <= L'9') || (c >= L'a' && c <= L'f') ||
          (c >= L'A' && c <= L'F'))) return 2;
    pin.push_back(static_cast<char>(c));
  }
  if (!ValidClientHash(pin)) return 2;
  std::string manifest, cms, multi, attached;
  if (!ReadFixture(root / L"manifest-fixture.json", &manifest) ||
      !ReadFixture(root / L"manifest-fixture.p7s", &cms) ||
      !ReadFixture(root / L"multi-fixture.p7s", &multi) ||
      !ReadFixture(root / L"attached-fixture.p7s", &attached)) return 3;
  if (ObserveSyntheticManifestSignature(manifest, cms, pin) !=
      ManifestSignatureObservation::kSignatureAndPinOnly) return 4;
  auto wrong = pin;
  wrong[0] = (wrong[0] == 'a' || wrong[0] == 'A') ? 'b' : 'a';
  if (ObserveSyntheticManifestSignature(manifest, cms, wrong) !=
      ManifestSignatureObservation::kSignerMismatch) return 5;
  auto changed = manifest;
  changed[0] ^= 1;
  if (ObserveSyntheticManifestSignature(changed, cms, pin) ==
      ManifestSignatureObservation::kSignatureAndPinOnly ||
      ObserveSyntheticManifestSignature(manifest + "\n", cms, pin) ==
      ManifestSignatureObservation::kSignatureAndPinOnly ||
      ObserveSyntheticManifestSignature(manifest, multi, pin) !=
      ManifestSignatureObservation::kMalformed) return 6;
  if (ObserveSyntheticManifestSignature(manifest, attached, pin) ==
      ManifestSignatureObservation::kSignatureAndPinOnly) return 7;
  for (const std::size_t size : {std::size_t(1), cms.size() / 2, cms.size() - 1})
    if (ObserveSyntheticManifestSignature(manifest, std::string_view(cms).substr(0, size), pin) ==
        ManifestSignatureObservation::kSignatureAndPinOnly) return 8;
  if (ObserveSyntheticManifestSignature("", cms, pin) != ManifestSignatureObservation::kRejected ||
      ObserveSyntheticManifestSignature(manifest, "", pin) != ManifestSignatureObservation::kRejected ||
      ObserveSyntheticManifestSignature(manifest, cms, std::string(64, '0')) !=
          ManifestSignatureObservation::kRejected ||
      ObserveSyntheticManifestSignature(std::string(kManifestFixtureLimit + 1, 'x'), cms, pin) !=
          ManifestSignatureObservation::kRejected ||
      ObserveSyntheticManifestSignature(manifest, std::string(kManifestFixtureLimit + 1, 'x'), pin) !=
          ManifestSignatureObservation::kRejected) return 9;
  ManifestClientEntry entry{"old", "old", "old", 999};
  if (ObserveSyntheticSignedEntry(manifest, cms, wrong, &entry) !=
      SignedEntryObservation::kSignatureRejected || entry.bytes != 999 ||
      ObserveSyntheticSignedEntry(changed, cms, pin, &entry) !=
      SignedEntryObservation::kSignatureRejected || entry.bytes != 999) return 10;
  if (ObserveSyntheticSignedEntry(manifest, cms, pin, &entry) !=
      SignedEntryObservation::kEntryOnly || entry.bytes != 123 ||
      entry.sha256 != std::string(64, 'a') ||
      entry.source_commit != std::string(40, 'b') || entry.version != "1.1.0-alpha.1") return 11;
  std::cout << "PASS: signed manifest bytes bound to synthetic client entry; no package authority\n";
  std::cout << "PASS: detached CMS bytes and independent DER SHA-256 pin, wrong pin, altered content, multiple signers, attached/truncated CMS and bounds refusal; no chain or package approval\n";
}
