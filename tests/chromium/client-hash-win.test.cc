// SPDX-License-Identifier: MPL-2.0
#include "../../chromium/dev/client-hash-win.h"
#include <iostream>
using namespace veil::chromium_dev;

int wmain(int argc, wchar_t** argv) {
  if (argc != 3) return 1;
  const std::wstring root(argv[1]);
  const std::wstring hash_arg(argv[2]);
  if (hash_arg.size() != 64) return 1;
  std::string hash;
  hash.reserve(hash_arg.size());
  for (const wchar_t c : hash_arg) {
    if (!((c >= L'0' && c <= L'9') || (c >= L'a' && c <= L'f') ||
          (c >= L'A' && c <= L'F'))) return 1;
    hash.push_back(static_cast<char>(c));
  }
  const auto path = root + L"\\client-fixture.exe";
  const auto alias = root + L"\\alias.exe";
  std::string out = "unchanged";
  if (ObserveSyntheticClientHash(root, root, hash, &out) !=
      ClientHashObservation::kMatchesOnly || !SameClientHash(out, hash)) return 2;
  out = "unchanged";
  if (ObserveSyntheticClientHash(root, root, std::string(64, '0'), &out) !=
      ClientHashObservation::kRejected ||
      ObserveSyntheticClientHash(root, root + L"\\other", hash, &out) !=
      ClientHashObservation::kRejected ||
      ObserveSyntheticClientHash(root, root, hash, nullptr) !=
      ClientHashObservation::kRejected || out != "unchanged") return 3;
  std::string wrong = hash;
  wrong[0] = wrong[0] == 'a' || wrong[0] == 'A' ? 'b' : 'a';
  if (ObserveSyntheticClientHash(root, root, wrong, &out) !=
      ClientHashObservation::kMismatch || out != "unchanged") return 4;
  if (!CreateHardLinkW(alias.c_str(), path.c_str(), nullptr)) return 5;
  const auto linked = ObserveSyntheticClientHash(root, root, hash, &out);
  if (!DeleteFileW(alias.c_str()) || linked != ClientHashObservation::kUnsafePath ||
      out != "unchanged") return 6;
  {
    config_path_detail::Handle writer(CreateFileW(path.c_str(), GENERIC_WRITE,
        FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr));
    if (!writer.valid() ||
        ObserveSyntheticClientHash(root, root, hash, &out) !=
          ClientHashObservation::kUnavailable || out != "unchanged") return 7;
  }
  if (!DeleteFileW(path.c_str()) ||
      ObserveSyntheticClientHash(root, root, hash, &out) !=
        ClientHashObservation::kUnavailable || out != "unchanged") return 8;
  auto write = [&](std::string_view bytes) {
    config_path_detail::Handle file(CreateFileW(path.c_str(), GENERIC_WRITE, 0,
        nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr));
    if (!file.valid()) return false;
    DWORD count = 0;
    return WriteFile(file.get(), bytes.data(), static_cast<DWORD>(bytes.size()),
                     &count, nullptr) && count == static_cast<DWORD>(bytes.size());
  };
  if (!write("") || ObserveSyntheticClientHash(root, root, hash, &out) !=
      ClientHashObservation::kSizeRejected || out != "unchanged") return 9;
  constexpr auto abc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
  if (!write("abc") || ObserveSyntheticClientHash(root, root, abc, &out) !=
      ClientHashObservation::kMatchesOnly || out != abc) return 10;
  if (!DeleteFileW(path.c_str())) return 11;
  std::cout << "PASS: CNG SHA-256 matches independent executable fixture hash and abc vector; wrong hash, hardlink, writer, missing and empty refusal; no signature or launch approval\n";
}
