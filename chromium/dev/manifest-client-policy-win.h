// SPDX-License-Identifier: MPL-2.0
// Explicit stronger synthetic policy path. Not package trust or launch authority.
#ifndef VEIL_CHROMIUM_DEV_MANIFEST_CLIENT_POLICY_WIN_H_
#define VEIL_CHROMIUM_DEV_MANIFEST_CLIENT_POLICY_WIN_H_
#include "manifest-client-win.h"
#include "manifest-chain-win.h"

namespace veil::chromium_dev {
inline SignedEntryObservation ObserveSyntheticOfflineSignedEntry(
    std::string_view manifest, std::string_view cms, std::string_view pin,
    ManifestClientEntry* output) {
  if (!output) return SignedEntryObservation::kRejected;
  if (ObserveSyntheticManifestOfflinePolicy(manifest, cms, pin) !=
      ManifestSignatureObservation::kSignatureAndPinOnly)
    return SignedEntryObservation::kSignatureRejected;
  if (ParseSyntheticManifestClient(manifest, output) != ManifestEntryResult::kEntryOnly)
    return SignedEntryObservation::kEntryRejected;
  return SignedEntryObservation::kEntryOnly;
}
inline ClientSignatureObservation ObserveOfflinePinnedClientHandle(
    HANDLE file, const std::wstring& path, std::string_view pin) {
  return client_signer_detail::ObservePinnedHandleWith(file, path, pin,
      [](PCCERT_CONTEXT cert) {
        return ObserveOfflineCodeSigningChain(cert) ==
            CertificateChainObservation::kPolicyMatchOnly;
      });
}
inline ManifestClientObservation ObserveSyntheticManifestClientOfflinePolicy(
    std::wstring_view expected_root, std::wstring_view selected_root,
    std::string_view manifest, std::string_view cms, std::string_view pin,
    ManifestClientEntry* output) {
  return ObserveSyntheticManifestClientWithPolicies(expected_root, selected_root,
      manifest, cms, pin, output, ObserveSyntheticOfflineSignedEntry,
      ObserveOfflinePinnedClientHandle);
}
}  // namespace veil::chromium_dev
#endif
