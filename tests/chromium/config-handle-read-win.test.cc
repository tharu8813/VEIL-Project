// SPDX-License-Identifier: MPL-2.0
#include "../../chromium/dev/config-path-win.h"
#include <iostream>
using namespace veil::chromium_dev;

int wmain(int argc, wchar_t** argv) {
  if (argc != 2) return 1;
  const std::wstring root(argv[1]);
  constexpr auto filename = L"client-fixture.json";
  const auto path = root + L"\\" + filename;
  auto write = [&](const std::string& value) {
    config_path_detail::Handle file(CreateFileW(path.c_str(), GENERIC_WRITE, 0,
        nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr));
    if (!file.valid()) return false;
    DWORD count = 0;
    return WriteFile(file.get(), value.data(), static_cast<DWORD>(value.size()),
                     &count, nullptr) && count == static_cast<DWORD>(value.size());
  };
  const std::string fixture = R"({"synthetic":true})";
  if (!write(fixture)) return 2;
  unsigned calls = 0;
  HANDLE first_file = nullptr;
  bool same = true;
  auto observe = [&](HANDLE handle, bool directory) {
    ++calls;
    if (!directory) {
      if (!first_file) first_file = handle;
      else same = same && first_file == handle;
    }
    return ConfigAclObservation::kObservedOnly; // synthetic injection only
  };
  std::string output = "unchanged";
  if (ObserveConfigPathWith(root, root, filename, observe, &output) !=
      ConfigPathObservation::kContentObservedOnly ||
      output != fixture || calls != 5 || !same) return 3;
  for (unsigned reject_at = 1; reject_at <= 5; ++reject_at) {
    unsigned seen = 0;
    auto reject = [&](HANDLE, bool) {
      return ++seen == reject_at ? ConfigAclObservation::kUntrustedWrite
                                : ConfigAclObservation::kObservedOnly;
    };
    output = "unchanged";
    if (ObserveConfigPathWith(root, root, filename, reject, &output) !=
        ConfigPathObservation::kAclRejected || seen != reject_at ||
        output != "unchanged") return 4;
  }
  const auto allow = [](HANDLE, bool) { return ConfigAclObservation::kObservedOnly; };
  const auto limit = config_path_detail::kConfigFixtureLimit;
  for (const auto& invalid : {std::string(), std::string(limit + 1u, 'x')}) {
    if (!write(invalid) ||
        ObserveConfigPathWith(root, root, filename, allow, &output) !=
          ConfigPathObservation::kSizeRejected || output != "unchanged") return 5;
  }
  const std::string boundary(limit, 'x');
  if (!write(boundary) ||
      ObserveConfigPathWith(root, root, filename, allow, &output) !=
          ConfigPathObservation::kContentObservedOnly || output != boundary) return 6;
  output = "unchanged";
  if (ObserveConfigPathWith(root, root, L"client.json", allow, &output) !=
          ConfigPathObservation::kLexicalRejected || output != "unchanged" ||
      ReadSyntheticConfigPath(root, root, filename, nullptr) !=
          ConfigPathObservation::kLexicalRejected) return 7;
  {
    config_path_detail::Handle writer(CreateFileW(path.c_str(), GENERIC_WRITE,
        FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL, nullptr));
    if (!writer.valid() ||
        ObserveConfigPathWith(root, root, filename, allow, &output) !=
          ConfigPathObservation::kUnavailable || output != "unchanged") return 8;
  }
  if (!write(fixture)) return 9;
  const auto actual = ReadSyntheticConfigPath(root, root, filename, &output);
  if (actual == ConfigPathObservation::kContentObservedOnly) {
    if (output != fixture) return 10;
  } else if (actual != ConfigPathObservation::kAclRejected ||
             output != "unchanged") return 11;
  if (!DeleteFileW(path.c_str())) return 12;
  std::cout << "PASS: bounded same-handle synthetic config read, ACL recheck, size boundary, sharing conflict and output preservation; no trusted config approval\n";
}
