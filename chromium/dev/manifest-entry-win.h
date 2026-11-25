// SPDX-License-Identifier: MPL-2.0
// Synthetic CMS-to-entry binding, not package trust.
#ifndef VEIL_CHROMIUM_DEV_MANIFEST_ENTRY_WIN_H_
#define VEIL_CHROMIUM_DEV_MANIFEST_ENTRY_WIN_H_
#include "manifest-entry-contract.h"
#include "manifest-signature-win.h"
namespace veil::chromium_dev {
static_assert(kManifestFixtureLimit == kManifestEntryLimit, "Manifest bounds must agree");
enum class SignedEntryObservation { kRejected, kSignatureRejected, kEntryRejected, kEntryOnly };
inline SignedEntryObservation ObserveSyntheticSignedEntry(
    std::string_view manifest, std::string_view cms, std::string_view pin,
    ManifestClientEntry* output) {
  if (!output) return SignedEntryObservation::kRejected;
  if (ObserveSyntheticManifestSignature(manifest, cms, pin) !=
      ManifestSignatureObservation::kSignatureAndPinOnly)
    return SignedEntryObservation::kSignatureRejected;
  if (ParseSyntheticManifestClient(manifest, output) != ManifestEntryResult::kEntryOnly)
    return SignedEntryObservation::kEntryRejected;
  return SignedEntryObservation::kEntryOnly;
}
}  // namespace veil::chromium_dev
#endif
