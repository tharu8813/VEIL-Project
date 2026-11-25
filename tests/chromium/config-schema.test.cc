// SPDX-License-Identifier: MPL-2.0
#include "../../chromium/dev/config-schema.h"
#include <iostream>
using namespace veil::chromium_dev;

int main() {
  const std::string valid = R"({
    "pipeName":"veil-fixture.01",
    "volumeId":"fixture-01",
    "brokerImage":"C:\\veil-dev\\broker.exe",
    "development":false,
    "requireWindowsService":true,
    "profilePath":"D:\\veil-dev\\Profile"
  })";
  ConfigSnapshot result;
  if (DecodeSyntheticConfig(valid, &result) != ConfigSchemaResult::kSnapshotOnly ||
      result.pipe_name != "veil-fixture.01" || result.volume_id != "fixture-01" ||
      result.broker_image != L"C:\\veil-dev\\broker.exe" ||
      result.profile_path != L"D:\\veil-dev\\Profile") return 1;
  if (DecodeSyntheticConfig("\xef\xbb\xbf" + valid, &result) !=
      ConfigSchemaResult::kSnapshotOnly) return 2;
  const auto mutate = [&](std::string_view from, std::string_view to) {
    std::string changed = valid;
    changed.replace(changed.find(from), from.size(), to);
    return changed;
  };
  const auto original = result;
  for (const auto& bad : {
      mutate("\"development\":false", "\"development\":true"),
      mutate("\"requireWindowsService\":true", "\"requireWindowsService\":false"),
      mutate("veil-fixture.01", "bad/name"),
      mutate("fixture-01", "bad id"),
      mutate("D:\\\\veil-dev\\\\Profile", "D:\\\\..\\\\Profile"),
      mutate("C:\\\\veil-dev\\\\broker.exe", "relative.exe"),
      mutate("D:\\\\veil-dev\\\\Profile", "\\\\\\\\server\\\\profile"),
      mutate("D:\\\\veil-dev\\\\Profile", "D:\\\\veil-dev\\\\Profile:stream")}) {
    if (DecodeSyntheticConfig(bad, &result) != ConfigSchemaResult::kRejected ||
        result.profile_path != original.profile_path ||
        result.pipe_name != original.pipe_name ||
        result.volume_id != original.volume_id ||
        result.broker_image != original.broker_image) return 3;
  }
  for (const auto& bad : {
      mutate("\"development\":false", "\"development\":\"false\""),
      mutate("\"profilePath\"", "\"volumeId\""),
      mutate("\"profilePath\"", "\"\\u0076olumeId\""),
      mutate("\"profilePath\"", "\"extra\""),
      mutate("\"profilePath\":\"D:\\\\veil-dev\\\\Profile\"", "\"profilePath\":null"),
      valid + valid, valid + ",", std::string(kConfigSchemaLimit + 1, ' '),
      std::string("\xef\xbb\xbf\xef\xbb\xbf") + valid}) {
    if (DecodeSyntheticConfig(bad, &result) != ConfigSchemaResult::kMalformed ||
        result.profile_path != original.profile_path) return 4;
  }
  for (std::size_t i = 0; i < valid.size(); ++i)
    if (DecodeSyntheticConfig(std::string_view(valid).substr(0, i), &result) !=
        ConfigSchemaResult::kMalformed) return 5;
  std::string boundary = valid;
  boundary.resize(kConfigSchemaLimit, ' ');
  if (DecodeSyntheticConfig(boundary, &result) != ConfigSchemaResult::kSnapshotOnly ||
      DecodeSyntheticConfig(valid, nullptr) != ConfigSchemaResult::kMalformed) return 6;
  // Existing wire parser still rejects embedded newlines despite shared reader change.
  BrokerStatusSnapshot wire;
  if (DecodeStatusFrame("{\"ok\":false,\n\"error\":\"AccessDenied\"}\n", &wire) !=
      BrokerStatusResult::kMalformed) return 7;
  std::cout << "PASS: six-field synthetic config schema, flags, strict path and identifier subset, BOM, duplicate and truncation rejection; no registration trust\n";
}
