// SPDX-License-Identifier: MPL-2.0
#include "../../chromium/dev/payload-tree-win.h"
#include <iostream>
using namespace veil::chromium_dev;
int wmain(int argc, wchar_t** argv) {
  if (argc != 3) return 1;
  const std::wstring root(argv[1]);
  const std::wstring_view mode(argv[2]);
  const auto client = root + L"\\Chromium\\client-fixture.exe";
  const auto alias = root + L"\\Chromium\\alias.exe";
  const auto extra = root + L"\\Chromium\\extra.bin";
  const auto resource = root + L"\\Broker\\nested\\resource.bin";
  const std::string abc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
  const std::string empty = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
  ManifestInventory expected{{std::string(40, 'b'), "1.1.0-alpha.1", abc, 3},
      {{"Chromium/client-fixture.exe", abc, 3}, {"Broker/nested/resource.bin", empty, 0}}};
  const std::vector<ManifestFileEntry> sentinel{{"sentinel", "old", 999}};
  auto out = sentinel;
  auto unchanged = [&] { return out.size() == 1 && out[0].path == "sentinel" &&
      out[0].sha256 == "old" && out[0].bytes == 999; };
  using Result = PayloadTreeObservation;
  auto scan = [&] { return ObserveSyntheticPayloadTree(root, root, &out); };
  if (mode == L"junction") {
    if (scan() != Result::kUnsafePath || !unchanged()) return 2;
    std::cout << "PASS: junction refused without publishing inventory\n";
    return 0;
  }
  if (mode != L"normal") return 3;
  if (scan() != Result::kObservedOnly ||
      CompareSyntheticInventory(expected, out) != InventoryComparison::kMatchesOnly) return 4;
  out = sentinel;
  if (ObserveSyntheticPayloadTree(root, root + L"\\other", &out) != Result::kRejected ||
      ObserveSyntheticPayloadTree(root, root, nullptr) != Result::kRejected || !unchanged()) return 5;
  if (!CreateHardLinkW(alias.c_str(), client.c_str(), nullptr)) return 6;
  const auto linked = scan();
  if (!DeleteFileW(alias.c_str()) || linked != Result::kUnsafePath || !unchanged()) return 7;
  {
    config_path_detail::Handle writer(CreateFileW(client.c_str(), GENERIC_WRITE,
        FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr));
    if (!writer.valid() || scan() != Result::kUnavailable || !unchanged()) return 8;
  }
  auto write = [](const std::wstring& path, std::string_view bytes) {
    config_path_detail::Handle file(CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr,
        CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr));
    DWORD count = 0;
    return file.valid() && WriteFile(file.get(), bytes.data(), static_cast<DWORD>(bytes.size()),
        &count, nullptr) && count == static_cast<DWORD>(bytes.size());
  };
  if (!write(extra, "abc") || scan() != Result::kObservedOnly ||
      CompareSyntheticInventory(expected, out) != InventoryComparison::kUnexpectedFile ||
      !DeleteFileW(extra.c_str())) return 9;
  if (!write(client, "abd") || scan() != Result::kObservedOnly ||
      CompareSyntheticInventory(expected, out) != InventoryComparison::kHashMismatch ||
      !write(client, "abc")) return 10;
  if (!DeleteFileW(resource.c_str()) || scan() != Result::kObservedOnly ||
      CompareSyntheticInventory(expected, out) != InventoryComparison::kMissingFile ||
      !write(resource, "")) return 11;
  const auto root_extra = root + L"\\extra.bin";
  out = sentinel;
  if (!write(root_extra, "abc") || scan() != Result::kUnsafePath || !unchanged() ||
      !DeleteFileW(root_extra.c_str())) return 12;
  if (scan() != Result::kObservedOnly ||
      CompareSyntheticInventory(expected, out) != InventoryComparison::kMatchesOnly) return 13;
  std::cout << "PASS: native nested payload scan, abc/empty SHA256, missing/extra/tampered comparison, hardlink/writer/root refusal; no package authorization\n";
}
