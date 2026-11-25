// SPDX-License-Identifier: MPL-2.0
#include "../../chromium/dev/client-integrity-win.h"
#include <iostream>
using namespace veil::chromium_dev;
int wmain(int argc, wchar_t** argv) {
  if (argc != 4) return 1;
  const std::wstring root(argv[1]);
  const std::wstring_view mode(argv[2]), hex(argv[3]);
  if (hex.size() != 64) return 1;
  std::string expected;
  for (const auto c : hex) {
    if (!((c >= L'0' && c <= L'9') || (c >= L'a' && c <= L'f') ||
          (c >= L'A' && c <= L'F'))) return 1;
    expected.push_back(static_cast<char>(c));
  }
  const auto path = root + L"\\client-fixture.exe";
  std::string output = "unchanged";
  auto wrong = expected;
  wrong[0] = (wrong[0] == 'a' || wrong[0] == 'A') ? 'b' : 'a';
  if (mode == L"signed") {
    if (ObserveSyntheticClientIntegrity(root, root, expected, &output) !=
        ClientIntegrityObservation::kObservationsOnly ||
        !SameClientHash(output, expected)) return 2;
    output = "unchanged";
    if (ObserveSyntheticClientIntegrity(root, root, wrong, &output) !=
        ClientIntegrityObservation::kHashMismatch || output != "unchanged") return 3;
    std::cout << "PASS: independently selected signed fixture combined hash/signature/hash observations; no signer pin\n";
    return 0;
  }
  if (mode != L"unsigned") return 4;
  if (ObserveSyntheticClientIntegrity(root, root, expected, &output) !=
      ClientIntegrityObservation::kSignatureRejected || output != "unchanged") return 5;
  unsigned calls = 0;
  auto observe = [&](HANDLE file, const std::wstring& name) {
    ++calls;
    LARGE_INTEGER offset{}, current{};
    if (name != path || !SetFilePointerEx(file, offset, &current, FILE_CURRENT) ||
        current.QuadPart != 0)
      return ClientSignatureObservation::kUnavailable;
    // Confirm the supplied live file handle actually hashes to expected.
    std::string hash;
    if (client_hash_detail::HashFixtureHandle(file, &hash) !=
        ClientHashObservation::kMatchesOnly || !SameClientHash(hash, expected))
      return ClientSignatureObservation::kUnavailable;
    return ClientSignatureObservation::kPolicyMatchOnly; // injected positive only
  };
  if (ObserveSyntheticClientIntegrityWith(root, root, wrong, &output, observe) !=
      ClientIntegrityObservation::kHashMismatch || calls != 0 ||
      output != "unchanged") return 6;
  if (ObserveSyntheticClientIntegrityWith(root, root, expected, &output, observe) !=
      ClientIntegrityObservation::kObservationsOnly || calls != 1 ||
      !SameClientHash(output, expected)) return 7;
  output = "unchanged";
  if (ObserveSyntheticClientIntegrityWith(root, root + L"\\other", expected, &output, observe) !=
      ClientIntegrityObservation::kRejected || calls != 1 ||
      output != "unchanged") return 8;
  const auto alias = root + L"\\alias.exe";
  if (!CreateHardLinkW(alias.c_str(), path.c_str(), nullptr)) return 9;
  const auto linked = ObserveSyntheticClientIntegrityWith(root, root, expected, &output, observe);
  if (!DeleteFileW(alias.c_str()) || linked != ClientIntegrityObservation::kUnsafePath ||
      calls != 1 || output != "unchanged") return 10;
  auto refusal = [](HANDLE, const std::wstring&) {
    return ClientSignatureObservation::kNotTrusted;
  };
  if (ObserveSyntheticClientIntegrityWith(root, root, expected, &output, refusal) !=
      ClientIntegrityObservation::kSignatureRejected || output != "unchanged") return 11;
  std::cout << "PASS: native unsigned refusal, mismatch-before-signature, same live handle injection, hardlink refusal and unchanged output; no authorization\n";
}
