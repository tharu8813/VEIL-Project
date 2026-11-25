// SPDX-License-Identifier: MPL-2.0
// Signed inventory snapshot only; no filesystem or authorization.
#ifndef VEIL_CHROMIUM_DEV_MANIFEST_INVENTORY_WIN_H_
#define VEIL_CHROMIUM_DEV_MANIFEST_INVENTORY_WIN_H_
#include "manifest-inventory-contract.h"
#include "manifest-chain-win.h"
#include "manifest-entry-win.h"

namespace veil::chromium_dev {
template <class SignatureObserver>
SignedEntryObservation ObserveSyntheticSignedInventoryWith(
    std::string_view manifest, std::string_view cms, std::string_view pin,
    ManifestInventory* output, SignatureObserver signature) {
  if (!output) return SignedEntryObservation::kRejected;
  if (signature(manifest, cms, pin) != ManifestSignatureObservation::kSignatureAndPinOnly)
    return SignedEntryObservation::kSignatureRejected;
  if (ParseSyntheticManifestInventory(manifest, output) != ManifestEntryResult::kEntryOnly)
    return SignedEntryObservation::kEntryRejected;
  return SignedEntryObservation::kEntryOnly;
}
inline SignedEntryObservation ObserveSyntheticOfflineInventory(
    std::string_view manifest, std::string_view cms, std::string_view pin,
    ManifestInventory* output) {
  return ObserveSyntheticSignedInventoryWith(manifest, cms, pin, output,
      ObserveSyntheticManifestOfflinePolicy);
}
}  // namespace veil::chromium_dev
#endif
