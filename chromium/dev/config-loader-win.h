// SPDX-License-Identifier: MPL-2.0
// Synthetic file-to-schema integration, not authenticated registration.
#ifndef VEIL_CHROMIUM_DEV_CONFIG_LOADER_WIN_H_
#define VEIL_CHROMIUM_DEV_CONFIG_LOADER_WIN_H_
#include "config-path-win.h"
#include "config-schema.h"

namespace veil::chromium_dev {
enum class ConfigLoadResult {
  kRejected, kPathRejected, kMalformed, kSchemaRejected, kSnapshotOnly,
};

static_assert(config_path_detail::kConfigFixtureLimit == kConfigSchemaLimit,
              "Synthetic read and parser limits must agree");

// Test injection cannot change the filename restriction or establish authority.
template <class AclObserver>
ConfigLoadResult LoadSyntheticConfigWith(
    std::wstring_view expected, std::wstring_view selected,
    ConfigSnapshot* output, AclObserver acl) {
  if (!output) return ConfigLoadResult::kRejected;
  std::string bytes;
  if (ObserveConfigPathWith(expected, selected, L"client-fixture.json", acl, &bytes) !=
      ConfigPathObservation::kContentObservedOnly)
    return ConfigLoadResult::kPathRejected;
  ConfigSnapshot candidate;
  switch (DecodeSyntheticConfig(bytes, &candidate)) {
    case ConfigSchemaResult::kMalformed: return ConfigLoadResult::kMalformed;
    case ConfigSchemaResult::kRejected: return ConfigLoadResult::kSchemaRejected;
    case ConfigSchemaResult::kSnapshotOnly: break;
    default: return ConfigLoadResult::kRejected;
  }
  *output = std::move(candidate);
  return ConfigLoadResult::kSnapshotOnly;
}

inline ConfigLoadResult LoadSyntheticConfig(
    std::wstring_view expected, std::wstring_view selected,
    ConfigSnapshot* output) {
  return LoadSyntheticConfigWith(expected, selected, output, ObserveConfigObjectHandle);
}
}  // namespace veil::chromium_dev
#endif
