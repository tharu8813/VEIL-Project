// SPDX-License-Identifier: MPL-2.0
#include "../../chromium/dev/manifest-inventory-contract.h"
#include <algorithm>
#include <iostream>
using namespace veil::chromium_dev;
int main() {
  const std::string hash(64, 'a'), second(64, 'B');
  const std::string json = "{\"format\":1,\"channel\":\"internal\",\"architecture\":\"win-x64\","
      "\"version\":\"1.1.0-alpha.1\",\"sourceCommit\":\"" + std::string(40, 'c') +
      "\",\"profileFormat\":1,\"files\":[{\"path\":\"Chromium/client-fixture.exe\","
      "\"bytes\":123,\"sha256\":\"" + hash + "\"},{\"path\":\"Broker/fixture.dll\","
      "\"bytes\":0,\"sha256\":\"" + second + "\"}]}";
  ManifestInventory expected;
  if (ParseSyntheticManifestInventory(json, &expected) != ManifestEntryResult::kEntryOnly ||
      expected.files.size() != 2 || expected.files[0].path != kSyntheticClientEntry ||
      expected.files[0].sha256 != hash || expected.files[0].bytes != 123 ||
      expected.files[1].path != "Broker/fixture.dll" || expected.files[1].bytes != 0 ||
      expected.files[1].sha256 != second) return 1;
  auto actual = expected.files;
  using Result = InventoryComparison;
  if (CompareSyntheticInventory(expected, actual) != Result::kMatchesOnly) return 2;
  std::reverse(actual.begin(), actual.end());
  actual[0].sha256 = std::string(64, 'b');
  if (CompareSyntheticInventory(expected, actual) != Result::kMatchesOnly) return 3;
  actual.pop_back();
  if (CompareSyntheticInventory(expected, actual) != Result::kMissingFile) return 4;
  actual = expected.files;
  actual.push_back({"Chromium/extra.dll", hash, 3});
  if (CompareSyntheticInventory(expected, actual) != Result::kUnexpectedFile) return 5;
  actual = expected.files;
  actual[0].path = "Chromium/CLIENT-FIXTURE.EXE";
  if (CompareSyntheticInventory(expected, actual) != Result::kPathMismatch) return 6;
  actual = expected.files;
  ++actual[0].bytes;
  if (CompareSyntheticInventory(expected, actual) != Result::kSizeMismatch) return 7;
  actual = expected.files;
  actual[0].sha256 = second;
  if (CompareSyntheticInventory(expected, actual) != Result::kHashMismatch) return 8;
  actual = expected.files;
  actual.push_back({"Chromium/CLIENT-FIXTURE.EXE", hash, 123});
  if (CompareSyntheticInventory(expected, actual) != Result::kInvalidObserved) return 9;
  actual = expected.files;
  actual.push_back({"Chromium/client-fixture.exe/child", hash, 0});
  if (CompareSyntheticInventory(expected, actual) != Result::kInvalidObserved) return 10;
  actual = expected.files;
  actual[0].path = "Chromium/../evil";
  if (CompareSyntheticInventory(expected, actual) != Result::kInvalidObserved) return 11;
  auto invalid = expected;
  invalid.client.bytes++;
  if (CompareSyntheticInventory(invalid, expected.files) != Result::kInvalidExpected) return 12;
  invalid = expected;
  invalid.files.erase(invalid.files.begin());
  if (CompareSyntheticInventory(invalid, expected.files) != Result::kInvalidExpected) return 13;
  if (ParseSyntheticManifestInventory(json + json, &expected) != ManifestEntryResult::kMalformed ||
      expected.files.size() != 2 || expected.client.bytes != 123 ||
      CompareSyntheticInventory(expected, expected.files) != Result::kMatchesOnly) return 14;
  std::cout << "PASS: full synthetic manifest inventory retained; missing/extra/path/size/hash/collision refusal and output preservation; no filesystem observation\n";
}
