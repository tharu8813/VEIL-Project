// SPDX-License-Identifier: MPL-2.0
// Compare synthetic inventory observations, not filesystem or package authority.
#ifndef VEIL_CHROMIUM_DEV_MANIFEST_INVENTORY_CONTRACT_H_
#define VEIL_CHROMIUM_DEV_MANIFEST_INVENTORY_CONTRACT_H_
#include "manifest-entry-contract.h"

namespace veil::chromium_dev {
enum class InventoryComparison {
  kInvalidExpected, kInvalidObserved, kUnexpectedFile, kMissingFile,
  kPathMismatch, kSizeMismatch, kHashMismatch, kMatchesOnly,
};

namespace manifest_inventory_detail {
using Index = std::map<std::string, const ManifestFileEntry*>;
inline bool IndexFiles(const std::vector<ManifestFileEntry>& files, Index* index) {
  if (files.size() > kManifestMaxEntries) return false;
  for (const auto& file : files) {
    if (!manifest_entry_detail::SafeRelative(file.path) ||
        !ValidClientHash(file.sha256) || file.bytes > 0x7fffffffffffffffULL ||
        !index->emplace(manifest_entry_detail::FoldPath(file.path), &file).second)
      return false;
  }
  for (const auto& record : *index) {
    const auto& name = record.first;
    for (auto pos = name.find('/'); pos != std::string::npos; pos = name.find('/', pos + 1))
      if (index->count(name.substr(0, pos))) return false;
  }
  return true;
}
}  // namespace manifest_inventory_detail

inline InventoryComparison CompareSyntheticInventory(
    const ManifestInventory& expected, const std::vector<ManifestFileEntry>& observed) {
  using namespace manifest_inventory_detail;
  Index planned, actual;
  if (!IndexFiles(expected.files, &planned)) return InventoryComparison::kInvalidExpected;
  const auto client = planned.find(manifest_entry_detail::FoldPath(std::string(kSyntheticClientEntry)));
  if (client == planned.end() || client->second->path != kSyntheticClientEntry ||
      !client->second->bytes || client->second->bytes > 512ULL * 1024u * 1024u ||
      client->second->bytes != expected.client.bytes ||
      !SameClientHash(client->second->sha256, expected.client.sha256) ||
      !manifest_entry_detail::HexCommit(expected.client.source_commit) ||
      !manifest_entry_detail::InternalVersion(expected.client.version))
    return InventoryComparison::kInvalidExpected;
  if (!IndexFiles(observed, &actual)) return InventoryComparison::kInvalidObserved;
  for (const auto& item : actual) {
    const auto found = planned.find(item.first);
    if (found == planned.end()) return InventoryComparison::kUnexpectedFile;
    const auto& a = *item.second;
    const auto& e = *found->second;
    if (a.path != e.path) return InventoryComparison::kPathMismatch;
    if (a.bytes != e.bytes) return InventoryComparison::kSizeMismatch;
    if (!SameClientHash(a.sha256, e.sha256)) return InventoryComparison::kHashMismatch;
  }
  if (actual.size() != planned.size()) return InventoryComparison::kMissingFile;
  return InventoryComparison::kMatchesOnly;
}
}  // namespace veil::chromium_dev
#endif
