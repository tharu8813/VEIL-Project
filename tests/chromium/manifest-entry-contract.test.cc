// SPDX-License-Identifier: MPL-2.0
#include "../../chromium/dev/manifest-entry-contract.h"
#include <iostream>
using namespace veil::chromium_dev;
int main() {
  const std::string hash(64, 'a'), commit(40, 'b');
  auto item = [&](std::string_view path, std::string_view bytes = "123") {
    return "{\"path\":\"" + std::string(path) + "\",\"bytes\":" +
        std::string(bytes) + ",\"sha256\":\"" + hash + "\"}";
  };
  const auto client = item(kSyntheticClientEntry);
  auto manifest = [&](const std::string& files) {
    return "{\"format\":1,\"channel\":\"internal\",\"architecture\":\"win-x64\","
        "\"version\":\"1.1.0-alpha.1\",\"sourceCommit\":\"" + commit +
        "\",\"profileFormat\":1,\"files\":[" + files + "]}";
  };
  const auto valid = manifest(client + "," + item("Broker/fixture.dll", "0"));
  ManifestClientEntry out;
  if (ParseSyntheticManifestClient(valid, &out) != ManifestEntryResult::kEntryOnly ||
      out.sha256 != hash || out.bytes != 123 || out.source_commit != commit ||
      out.version != "1.1.0-alpha.1") return 1;
  const ManifestClientEntry sentinel{"old", "old", "old", 999};
  out = sentinel;
  auto unchanged = [&] {
    return out.sha256 == sentinel.sha256 && out.source_commit == sentinel.source_commit &&
        out.version == sentinel.version && out.bytes == sentinel.bytes;
  };
  auto mutate = [&](std::string_view from, std::string_view to) {
    auto s = valid;
    s.replace(s.find(from), from.size(), to);
    return s;
  };
  for (const auto& bad : {
      manifest(client + "," + client),
      manifest(client + "," + item("Chromium/CLIENT-FIXTURE.EXE")),
      manifest(client + "," + item("Chromium/client-fixture.exe/sub")),
      manifest(client + "," + item("Chromium/../x")),
      manifest(client + "," + item("Chromium/NUL.txt")),
      manifest(client + "," + item("Desktop/Veil.Desktop.exe")),
      manifest(item(kSyntheticClientEntry, "0")),
      manifest(item(kSyntheticClientEntry, "536870913")),
      manifest(item(kSyntheticClientEntry, "01")),
      manifest(item(kSyntheticClientEntry, "-1")),
      manifest(item(kSyntheticClientEntry, "1.5")),
      manifest(item(kSyntheticClientEntry, "1e2")),
      manifest(item(kSyntheticClientEntry, "9223372036854775808")),
      mutate("\"profileFormat\":1", "\"profileFormat\":2"),
      mutate("\"architecture\":\"win-x64\"", "\"architecture\":\"win-arm64\""),
      mutate("\"version\":\"1.1.0-alpha.1\"", "\"version\":\"1.1.0\""),
      mutate("\"version\":\"1.1.0-alpha.1\"", "\"version\":\"65536.1.0-alpha.1\""),
      mutate("\"profileFormat\":1", "\"format\":1"),
      mutate(hash, std::string(64, 'z')), valid + valid}) {
    if (ParseSyntheticManifestClient(bad, &out) != ManifestEntryResult::kMalformed ||
        !unchanged()) return 2;
  }
  if (ParseSyntheticManifestClient(manifest(item("Chromium/other.exe")), &out) !=
      ManifestEntryResult::kNoClient || !unchanged()) return 3;
  for (std::size_t i = 0; i < valid.size(); ++i)
    if (ParseSyntheticManifestClient(std::string_view(valid).substr(0, i), &out) ==
        ManifestEntryResult::kEntryOnly || !unchanged()) return 4;
  std::string many = client;
  for (std::size_t i = 1; i <= kManifestMaxEntries; ++i)
    many += "," + item("Chromium/file-" + std::to_string(i));
  if (ParseSyntheticManifestClient(manifest(many), &out) != ManifestEntryResult::kMalformed ||
      !unchanged()) return 5;
  if (ParseSyntheticManifestClient(std::string(kManifestEntryLimit + 1, ' '), &out) !=
      ManifestEntryResult::kRejected || ParseSyntheticManifestClient(valid, nullptr) !=
      ManifestEntryResult::kRejected || !unchanged()) return 6;
  std::cout << "PASS: strict synthetic manifest entry selection, duplicate/collision/path/version/size rejection and output preservation; no package trust\n";
}
