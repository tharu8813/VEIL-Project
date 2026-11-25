// SPDX-License-Identifier: MPL-2.0
// Synthetic signed manifest-to-executable binding; not package authorization.
#ifndef VEIL_CHROMIUM_DEV_MANIFEST_CLIENT_WIN_H_
#define VEIL_CHROMIUM_DEV_MANIFEST_CLIENT_WIN_H_
#include "manifest-entry-win.h"

namespace veil::chromium_dev {
enum class ManifestClientObservation {
  kRejected, kManifestRejected, kClientRejected, kSizeMismatch,
  kHashMismatch, kSignatureRejected, kObservationsOnly,
};

template <class PinnedObserver>
ManifestClientObservation ObserveSyntheticManifestClientWith(
    std::wstring_view expected_root, std::wstring_view selected_root,
    std::string_view manifest, std::string_view cms, std::string_view pin,
    ManifestClientEntry* output, PinnedObserver signer) {
  if (!output || !ValidClientHash(pin) ||
      Evaluate(expected_root, selected_root, false, false) !=
          PathDecision::kLexicalMatchOnly)
    return ManifestClientObservation::kRejected;
  ManifestClientEntry entry;
  if (ObserveSyntheticSignedEntry(manifest, cms, pin, &entry) !=
      SignedEntryObservation::kEntryOnly)
    return ManifestClientObservation::kManifestRejected;
  // Bind the exact fixed relative entry to a fixed subdirectory. No caller
  // supplied executable path, size, hash or second signer pin.
  const std::wstring directory = std::wstring(selected_root) + L"\\Chromium";
  if (!SafeDedicatedPath(directory)) return ManifestClientObservation::kRejected;
  std::string hash;
  const auto result = ObserveSyntheticClientIntegrityWith(
      directory, directory, entry.sha256, &hash,
      [&](HANDLE file, const std::wstring& path) {
        return signer(file, path, pin);
      }, entry.bytes);
  switch (result) {
    case ClientIntegrityObservation::kSizeMismatch:
      return ManifestClientObservation::kSizeMismatch;
    case ClientIntegrityObservation::kHashMismatch:
      return ManifestClientObservation::kHashMismatch;
    case ClientIntegrityObservation::kSignatureRejected:
      return ManifestClientObservation::kSignatureRejected;
    case ClientIntegrityObservation::kObservationsOnly:
      break;
    default: return ManifestClientObservation::kClientRejected;
  }
  if (!SameClientHash(hash, entry.sha256))
    return ManifestClientObservation::kClientRejected;
  *output = std::move(entry);
  return ManifestClientObservation::kObservationsOnly;
}

inline ManifestClientObservation ObserveSyntheticManifestClient(
    std::wstring_view expected_root, std::wstring_view selected_root,
    std::string_view manifest, std::string_view cms, std::string_view pin,
    ManifestClientEntry* output) {
  return ObserveSyntheticManifestClientWith(expected_root, selected_root,
      manifest, cms, pin, output, client_signer_detail::ObservePinnedHandle);
}
}  // namespace veil::chromium_dev
#endif
