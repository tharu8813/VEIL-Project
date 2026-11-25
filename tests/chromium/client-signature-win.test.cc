// SPDX-License-Identifier: MPL-2.0
#include "../../chromium/dev/client-signature-win.h"
#include <iostream>
using namespace veil::chromium_dev;

int wmain(int argc, wchar_t** argv) {
  if (argc != 3) return 1;
  const std::wstring root(argv[1]);
  const std::wstring_view mode(argv[2]);
  const auto path = root + L"\\client-fixture.exe";
  if (mode == L"signed") {
    if (ObserveSyntheticClientSignature(root, root) !=
        ClientSignatureObservation::kPolicyMatchOnly) return 2;
    // Mutate only the copied fixture, not the source signed executable.
    {
      config_path_detail::Handle writer(CreateFileW(path.c_str(), GENERIC_WRITE,
          0, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr));
      if (!writer.valid()) return 3;
      constexpr char invalid[] = "XX";
      DWORD count = 0;
      if (!WriteFile(writer.get(), invalid, 2, &count, nullptr) || count != 2) return 4;
    }
    if (ObserveSyntheticClientSignature(root, root) !=
        ClientSignatureObservation::kNotTrusted) return 5;
    std::cout << "PASS: independently selected signed fixture policy match and corrupted copy refusal; no VEIL signer pin\n";
    return 0;
  }
  if (mode != L"unsigned") return 6;
  if (ObserveSyntheticClientSignature(root, root) !=
      ClientSignatureObservation::kNotTrusted ||
      ObserveSyntheticClientSignature(root, root + L"\\other") !=
      ClientSignatureObservation::kRejected) return 7;
  const auto alias = root + L"\\alias.exe";
  if (!CreateHardLinkW(alias.c_str(), path.c_str(), nullptr)) return 8;
  const auto linked = ObserveSyntheticClientSignature(root, root);
  if (!DeleteFileW(alias.c_str()) ||
      linked != ClientSignatureObservation::kUnsafePath) return 9;
  {
    config_path_detail::Handle writer(CreateFileW(path.c_str(), GENERIC_WRITE,
        FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL, nullptr));
    if (!writer.valid() || ObserveSyntheticClientSignature(root, root) !=
        ClientSignatureObservation::kUnavailable) return 10;
  }
  if (!DeleteFileW(path.c_str()) ||
      ObserveSyntheticClientSignature(root, root) !=
        ClientSignatureObservation::kUnavailable) return 11;
  auto write = [&](std::string_view bytes) {
    config_path_detail::Handle file(CreateFileW(path.c_str(), GENERIC_WRITE, 0,
        nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr));
    if (!file.valid()) return false;
    DWORD count = 0;
    return WriteFile(file.get(), bytes.data(), static_cast<DWORD>(bytes.size()),
                     &count, nullptr) && count == static_cast<DWORD>(bytes.size());
  };
  if (!write("") || ObserveSyntheticClientSignature(root, root) !=
      ClientSignatureObservation::kSizeRejected || !DeleteFileW(path.c_str())) return 12;
  if (!write("not a PE executable") ||
      ObserveSyntheticClientSignature(root, root) != ClientSignatureObservation::kNotTrusted ||
      !DeleteFileW(path.c_str())) return 13;
  std::cout << "PASS: unsigned executable, non-PE, empty, hardlink, writer and missing fixture refusal; no signature or launch approval\n";
}
