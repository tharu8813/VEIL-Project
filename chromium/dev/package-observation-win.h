// SPDX-License-Identifier: MPL-2.0
// Synthetic package payload observation only. Not installation or launch authority.
#ifndef VEIL_CHROMIUM_DEV_PACKAGE_OBSERVATION_WIN_H_
#define VEIL_CHROMIUM_DEV_PACKAGE_OBSERVATION_WIN_H_
#include "manifest-inventory-win.h"
#include "manifest-client-policy-win.h"
#include "payload-tree-win.h"

namespace veil::chromium_dev {
enum class PackageObservation {
  kRejected, kManifestRejected, kTreeRejected, kInventoryMismatch,
  kClientRejected, kRescanRejected, kObservationsOnly,
};

template <class SignatureObserver, class PinnedObserver>
PackageObservation ObserveSyntheticPackageWith(
    std::wstring_view expected_root, std::wstring_view selected_root,
    std::string_view manifest, std::string_view cms, std::string_view pin,
    ManifestInventory* output, SignatureObserver signature, PinnedObserver signer) {
  if (!output || !ValidClientHash(pin) ||
      Evaluate(expected_root, selected_root, false, false) != PathDecision::kLexicalMatchOnly)
    return PackageObservation::kRejected;
  ManifestInventory expected;
  if (ObserveSyntheticSignedInventoryWith(manifest, cms, pin, &expected, signature) !=
      SignedEntryObservation::kEntryOnly)
    return PackageObservation::kManifestRejected;
  std::vector<ManifestFileEntry> observed;
  if (ObserveSyntheticPayloadTree(expected_root, selected_root, &observed) !=
      PayloadTreeObservation::kObservedOnly)
    return PackageObservation::kTreeRejected;
  if (CompareSyntheticInventory(expected, observed) != InventoryComparison::kMatchesOnly)
    return PackageObservation::kInventoryMismatch;
  // Use the exact entry from the already verified signed bytes, not a caller
  // supplied size/hash or a second independently parsed manifest.
  const auto directory = std::wstring(selected_root) + L"\\Chromium";
  if (!SafeDedicatedPath(directory)) return PackageObservation::kClientRejected;
  std::string digest;
  if (ObserveSyntheticClientIntegrityWith(directory, directory, expected.client.sha256,
      &digest, [&](HANDLE file, const std::wstring& path) {
        return signer(file, path, pin);
      }, expected.client.bytes) != ClientIntegrityObservation::kObservationsOnly ||
      !SameClientHash(digest, expected.client.sha256))
    return PackageObservation::kClientRejected;
  // Detect persistent differences across the client check. Not a snapshot,
  // transactional directory lock or proof against temporary replacements.
  observed.clear();
  if (ObserveSyntheticPayloadTree(expected_root, selected_root, &observed) !=
      PayloadTreeObservation::kObservedOnly ||
      CompareSyntheticInventory(expected, observed) != InventoryComparison::kMatchesOnly)
    return PackageObservation::kRescanRejected;
  *output = std::move(expected);
  return PackageObservation::kObservationsOnly;
}

inline PackageObservation ObserveSyntheticPackageOfflinePolicy(
    std::wstring_view expected_root, std::wstring_view selected_root,
    std::string_view manifest, std::string_view cms, std::string_view pin,
    ManifestInventory* output) {
  return ObserveSyntheticPackageWith(expected_root, selected_root, manifest, cms,
      pin, output, ObserveSyntheticManifestOfflinePolicy, ObserveOfflinePinnedClientHandle);
}
}  // namespace veil::chromium_dev
#endif
