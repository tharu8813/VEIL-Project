// SPDX-License-Identifier: MPL-2.0
#include "../../chromium/dev/config-loader-win.h"
#include <iostream>
using namespace veil::chromium_dev;

int wmain(int argc, wchar_t** argv) {
  if (argc != 2) return 1;
  const std::wstring root(argv[1]);
  const auto path = root + L"\\client-fixture.json";
  const auto alias = root + L"\\alias.json";
  const std::string valid = R"({
    "pipeName":"veil-fixture.01",
    "volumeId":"fixture-01",
    "brokerImage":"C:\\synthetic\\broker.exe",
    "development":false,
    "requireWindowsService":true,
    "profilePath":"D:\\synthetic\\Profile"
  })";
  auto write = [&](const std::string& bytes) {
    config_path_detail::Handle file(CreateFileW(path.c_str(), GENERIC_WRITE, 0,
        nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr));
    if (!file.valid()) return false;
    DWORD count = 0;
    return WriteFile(file.get(), bytes.data(), static_cast<DWORD>(bytes.size()),
                     &count, nullptr) && count == static_cast<DWORD>(bytes.size());
  };
  const ConfigSnapshot sentinel{"unchanged-pipe", "unchanged-volume",
      L"unchanged-image", L"unchanged-profile"};
  ConfigSnapshot output = sentinel;
  auto unchanged = [&] {
    return output.pipe_name == sentinel.pipe_name &&
        output.volume_id == sentinel.volume_id &&
        output.broker_image == sentinel.broker_image &&
        output.profile_path == sentinel.profile_path;
  };
  auto valid_output = [&] {
    return output.pipe_name == "veil-fixture.01" && output.volume_id == "fixture-01" &&
        output.broker_image == L"C:\\synthetic\\broker.exe" &&
        output.profile_path == L"D:\\synthetic\\Profile";
  };
  unsigned calls = 0;
  auto allow = [&](HANDLE, bool) {
    ++calls;
    return ConfigAclObservation::kObservedOnly; // injected fixture only
  };
  if (LoadSyntheticConfigWith(root, root, &output, allow) !=
      ConfigLoadResult::kPathRejected || !unchanged()) return 2;
  if (!write(valid)) return 3;
  calls = 0;
  if (LoadSyntheticConfigWith(root, root, &output, allow) !=
      ConfigLoadResult::kSnapshotOnly || calls != 5 || !valid_output()) return 4;
  output = sentinel;
  for (unsigned at = 1; at <= 5; ++at) {
    unsigned seen = 0;
    auto reject = [&](HANDLE, bool) {
      return ++seen == at ? ConfigAclObservation::kUntrustedWrite
                          : ConfigAclObservation::kObservedOnly;
    };
    if (LoadSyntheticConfigWith(root, root, &output, reject) !=
        ConfigLoadResult::kPathRejected || seen != at || !unchanged()) return 5;
  }
  auto invalid = valid;
  invalid.replace(invalid.find("\"development\":false"),
                  std::string("\"development\":false").size(), "\"development\":true");
  if (!write(invalid) ||
      LoadSyntheticConfigWith(root, root, &output, allow) !=
        ConfigLoadResult::kSchemaRejected || !unchanged()) return 6;
  if (!write(valid.substr(0, valid.size() - 1)) ||
      LoadSyntheticConfigWith(root, root, &output, allow) !=
        ConfigLoadResult::kMalformed || !unchanged()) return 7;
  if (!write(std::string(kConfigSchemaLimit + 1, 'x')) ||
      LoadSyntheticConfigWith(root, root, &output, allow) !=
        ConfigLoadResult::kPathRejected || !unchanged()) return 8;
  if (!write(valid) || !CreateHardLinkW(alias.c_str(), path.c_str(), nullptr)) return 9;
  const auto linked = LoadSyntheticConfigWith(root, root, &output, allow);
  if (!DeleteFileW(alias.c_str()) || linked != ConfigLoadResult::kPathRejected ||
      !unchanged()) return 10;
  if (LoadSyntheticConfigWith(root, root + L"\\other", &output, allow) !=
      ConfigLoadResult::kPathRejected || !unchanged() ||
      LoadSyntheticConfigWith(root, root, nullptr, allow) !=
      ConfigLoadResult::kRejected) return 11;
  const auto native = LoadSyntheticConfig(root, root, &output);
  if (native == ConfigLoadResult::kSnapshotOnly) {
    if (!valid_output()) return 12;
  } else if (native != ConfigLoadResult::kPathRejected || !unchanged()) return 13;
  if (!DeleteFileW(path.c_str())) return 14;
  std::cout << "PASS: synthetic file-to-schema integration, ACL/path/size/malformed/schema rejection and output preservation; no registration authority or broker connection\n";
}
