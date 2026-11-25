// SPDX-License-Identifier: MPL-2.0
#include "../../chromium/dev/broker-status-wire.h"
#include <iostream>
using namespace veil::chromium_dev;

int main() {
  std::string request = "unchanged";
  if (!EncodeStatusRequest("synthetic-01", &request) ||
      request != "{\"command\":\"Status\",\"volumeId\":\"synthetic-01\"}\n") return 1;
  for (const auto& invalid : {std::string(), std::string(65, 'x'), std::string("a\"b"),
       std::string("a\\b"), std::string("a\nb"), std::string("a b"), std::string("a\0b", 3)}) {
    request = "unchanged";
    if (EncodeStatusRequest(invalid, &request) || request != "unchanged") return 2;
  }
  if (EncodeStatusRequest("valid", nullptr)) return 3;
  const std::string good = R"({"ok":true,"status":{"attached":true,"volumeGuid":"\\\\?\\Volume{a1234567-1234-1234-1234-123456789abc}\\","fullyEncrypted":true,"protectionOn":true,"unlocked":true,"readyToUnlock":false,"protectorPresent":true}})" "\n";
  const std::string detached = R"({"status":{"protectorPresent":false,"readyToUnlock":false,"unlocked":false,"protectionOn":false,"fullyEncrypted":false,"volumeGuid":null,"attached":false},"ok":true})" "\n";
  BrokerStatusSnapshot snapshot;
  if (DecodeStatusFrame(good, &snapshot) != BrokerStatusResult::kSnapshotOnly ||
      !snapshot.attached || !snapshot.fully_encrypted || !snapshot.protection_on ||
      !snapshot.unlocked || snapshot.ready_to_unlock || !snapshot.protector_present ||
      !IsVolumeGuid(snapshot.volume_guid)) return 4;
  if (DecodeStatusFrame(detached, &snapshot) != BrokerStatusResult::kSnapshotOnly ||
      snapshot.attached || !snapshot.volume_guid.empty() || snapshot.unlocked) return 5;
  if (DecodeStatusFrame(good, &snapshot) != BrokerStatusResult::kSnapshotOnly) return 6;
  const auto original = snapshot.volume_guid;
  if (DecodeStatusFrame("{\"ok\":false,\"error\":\"AccessDenied\"}\n", &snapshot) !=
      BrokerStatusResult::kRefused || snapshot.volume_guid != original) return 7;
  for (std::size_t i = 0; i < good.size(); ++i)
    if (DecodeStatusFrame(std::string_view(good).substr(0, i), &snapshot) !=
        BrokerStatusResult::kMalformed || snapshot.volume_guid != original) return 8;
  auto mutate = [&](std::string_view from, std::string_view to) {
    std::string changed = good;
    changed.replace(changed.find(from), from.size(), to);
    return changed;
  };
  for (const auto& invalid : {mutate("\"attached\":true", "\"attached\":1"),
       mutate("\"attached\":true", "\"attached\":false"),
       mutate("\"unlocked\":true", "\"attached\":true"),
       mutate("\"unlocked\":true", "\"extra\":true"),
       mutate("\"unlocked\":true,", ""),
       mutate("\"ok\":true", "\"ok\":false"),
       mutate("\"volumeGuid\":\"", "\"volumeGuid\":null,\"extra\":\""),
       good + good, std::string(4097, ' ') + "\n",
       std::string("{\"ok\":true,\"ok\":true}\n"),
       std::string("{\"ok\":false,\"error\":\"\"}\n")}) {
    if (DecodeStatusFrame(invalid, &snapshot) != BrokerStatusResult::kMalformed ||
        !snapshot.attached || snapshot.volume_guid != original) return 9;
  }
  const auto escaped = mutate("\"ok\"", "\"\\u006fk\"");
  if (DecodeStatusFrame(escaped, &snapshot) != BrokerStatusResult::kSnapshotOnly) return 10;
  // Bounded payload including permitted trailing whitespace.
  std::string bounded = good.substr(0, good.size() - 1);
  bounded.resize(kBrokerPayloadLimit, ' ');
  bounded += '\n';
  if (DecodeStatusFrame(bounded, &snapshot) != BrokerStatusResult::kSnapshotOnly) return 11;
  bounded.insert(bounded.size() - 1, 1, ' ');
  if (DecodeStatusFrame(bounded, &snapshot) != BrokerStatusResult::kMalformed) return 12;
  if (DecodeStatusFrame(good, nullptr) != BrokerStatusResult::kMalformed) return 13;
  std::cout << "PASS: bounded Status-only wire fixtures, schema and truncation rejection; no pipe or authentication\n";
}
