// SPDX-License-Identifier: MPL-2.0
// Standalone synthetic registration schema; no trusted registration or I/O.
#ifndef VEIL_CHROMIUM_DEV_CONFIG_SCHEMA_H_
#define VEIL_CHROMIUM_DEV_CONFIG_SCHEMA_H_
#include "broker-status-wire.h"
#include <utility>

namespace veil::chromium_dev {
inline constexpr std::size_t kConfigSchemaLimit = 16 * 1024;
struct ConfigSnapshot {
  std::string pipe_name;
  std::string volume_id;
  std::wstring broker_image;
  std::wstring profile_path;
};
enum class ConfigSchemaResult { kMalformed, kRejected, kSnapshotOnly };

inline bool IsSyntheticPipeName(std::string_view name) {
  if (name.empty() || name.size() > 128) return false;
  for (const char c : name)
    if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
          (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.'))
      return false;
  return true;
}

// Require all six explicit legacy fields. This deliberately accepts only a
// conservative ASCII subset and product-shaped flags, not arbitrary JSON.
inline ConfigSchemaResult DecodeSyntheticConfig(std::string_view json,
                                                 ConfigSnapshot* output) {
  if (!output || json.empty() || json.size() > kConfigSchemaLimit)
    return ConfigSchemaResult::kMalformed;
  // Accept a single UTF-8 BOM, matching the legacy text reader for this subset.
  if (json.substr(0, 3) == "\xef\xbb\xbf") json.remove_prefix(3);
  status_wire_detail::Reader reader(json);
  if (!reader.Take('{')) return ConfigSchemaResult::kMalformed;
  constexpr std::array<std::string_view, 6> names{
      "pipeName", "volumeId", "brokerImage", "development",
      "requireWindowsService", "profilePath"};
  unsigned seen = 0;
  bool development = true;
  bool require_service = false;
  ConfigSnapshot snapshot;
  for (std::size_t count = 0; count < names.size(); ++count) {
    if (count && !reader.Take(',')) return ConfigSchemaResult::kMalformed;
    std::string key;
    if (!reader.String(&key) || !reader.Take(':')) return ConfigSchemaResult::kMalformed;
    std::size_t i = 0;
    while (i < names.size() && names[i] != key) ++i;
    if (i == names.size() || (seen & (1u << i))) return ConfigSchemaResult::kMalformed;
    seen |= 1u << i;
    std::string value;
    switch (i) {
      case 0: if (!reader.String(&snapshot.pipe_name)) return ConfigSchemaResult::kMalformed; break;
      case 1: if (!reader.String(&snapshot.volume_id)) return ConfigSchemaResult::kMalformed; break;
      case 2:
        if (!reader.String(&value)) return ConfigSchemaResult::kMalformed;
        snapshot.broker_image.assign(value.begin(), value.end());
        break;
      case 3: if (!reader.Bool(&development)) return ConfigSchemaResult::kMalformed; break;
      case 4: if (!reader.Bool(&require_service)) return ConfigSchemaResult::kMalformed; break;
      case 5:
        if (!reader.String(&value)) return ConfigSchemaResult::kMalformed;
        snapshot.profile_path.assign(value.begin(), value.end());
        break;
      default: return ConfigSchemaResult::kMalformed;
    }
  }
  if (seen != 63 || !reader.Take('}') || !reader.End())
    return ConfigSchemaResult::kMalformed;
  std::string request;
  if (development || !require_service || !IsSyntheticPipeName(snapshot.pipe_name) ||
      !EncodeStatusRequest(snapshot.volume_id, &request) ||
      !SafeDedicatedPath(snapshot.broker_image) ||
      !SafeDedicatedPath(snapshot.profile_path))
    return ConfigSchemaResult::kRejected;
  // No paths are opened and no pipe connection can be made by this parser.
  *output = std::move(snapshot);
  return ConfigSchemaResult::kSnapshotOnly;
}
}  // namespace veil::chromium_dev
#endif
