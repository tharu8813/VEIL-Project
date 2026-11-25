// SPDX-License-Identifier: MPL-2.0
#include "../../chromium/dev/client-signer-win.h"
#include <iostream>
using namespace veil::chromium_dev;

bool ReadHex(std::wstring_view input, std::string* output) {
  if (input.size() != 64) return false;
  for (const auto c : input) {
    if (!((c >= L'0' && c <= L'9') || (c >= L'a' && c <= L'f') ||
          (c >= L'A' && c <= L'F'))) return false;
    output->push_back(static_cast<char>(c));
  }
  return ValidClientHash(*output);
}
int wmain(int argc, wchar_t** argv) {
  if (argc != 5) return 1;
  const std::wstring root(argv[1]);
  const std::wstring_view mode(argv[2]);
  std::string hash, pin;
  if (!ReadHex(argv[3], &hash) || !ReadHex(argv[4], &pin)) return 2;
  std::string output = "unchanged";
  if (ObserveSyntheticPinnedClient(root, root, hash, "", &output) !=
      ClientIntegrityObservation::kRejected ||
      ObserveSyntheticPinnedClient(root, root, hash, std::string(64, '0'), &output) !=
      ClientIntegrityObservation::kRejected || output != "unchanged") return 3;
  if (mode == L"unsigned") {
    if (ObserveSyntheticPinnedClient(root, root, hash, pin, &output) !=
        ClientIntegrityObservation::kSignatureRejected || output != "unchanged") return 4;
    std::cout << "PASS: unsigned file with correct hash and arbitrary pin refused; malformed pins refused\n";
    return 0;
  }
  if (mode != L"signed") return 5;
  if (ObserveSyntheticPinnedClient(root, root, hash, pin, &output) !=
      ClientIntegrityObservation::kObservationsOnly || !SameClientHash(hash, output)) return 6;
  output = "unchanged";
  auto wrong = pin;
  wrong[0] = (wrong[0] == 'a' || wrong[0] == 'A') ? 'b' : 'a';
  if (ObserveSyntheticPinnedClient(root, root, hash, wrong, &output) !=
      ClientIntegrityObservation::kSignatureRejected || output != "unchanged") return 7;
  auto wrong_hash = hash;
  wrong_hash[0] = (wrong_hash[0] == 'a' || wrong_hash[0] == 'A') ? 'b' : 'a';
  if (ObserveSyntheticPinnedClient(root, root, wrong_hash, pin, &output) !=
      ClientIntegrityObservation::kHashMismatch || output != "unchanged") return 8;
  if (ObserveSyntheticPinnedClient(root, root, hash, pin, nullptr) !=
      ClientIntegrityObservation::kRejected) return 9;
  std::cout << "PASS: verified primary signer DER SHA-256 matches independent certificate digest; wrong signer and hash refused; no VEIL pin\n";
}
