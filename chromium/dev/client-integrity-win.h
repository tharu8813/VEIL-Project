// SPDX-License-Identifier: MPL-2.0
// Synthetic combined file integrity observation, not authorization.
#ifndef VEIL_CHROMIUM_DEV_CLIENT_INTEGRITY_WIN_H_
#define VEIL_CHROMIUM_DEV_CLIENT_INTEGRITY_WIN_H_
#include "client-signature-win.h"

namespace veil::chromium_dev {
enum class ClientIntegrityObservation {
  kRejected, kUnavailable, kUnsafePath, kHashRejected, kHashMismatch,
  kSignatureRejected, kChanged, kObservationsOnly, kSizeMismatch,
};

template <class SignatureObserver>
ClientIntegrityObservation ObserveSyntheticClientIntegrityWith(
    std::wstring_view expected_root, std::wstring_view selected_root,
    std::string_view expected_hash, std::string* observed,
    SignatureObserver signature, std::uint64_t expected_bytes = 0) {
  if (!observed || !ValidClientHash(expected_hash) ||
      Evaluate(expected_root, selected_root, false, false) !=
          PathDecision::kLexicalMatchOnly) return ClientIntegrityObservation::kRejected;
  const std::wstring path = std::wstring(selected_root) + L"\\client-fixture.exe";
  if (!SafeDedicatedPath(path)) return ClientIntegrityObservation::kRejected;
  DirectoryHandleRetention retained;
  if (retained.Observe(expected_root, selected_root, false, false) !=
      FilesystemObservation::kObservedOnly) return ClientIntegrityObservation::kUnsafePath;
  config_path_detail::Handle file(CreateFileW(path.c_str(), GENERIC_READ,
      FILE_SHARE_READ, nullptr, OPEN_EXISTING,
      FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS, nullptr));
  if (!file.valid()) return ClientIntegrityObservation::kUnavailable;
  if (config_path_detail::ObserveFileShape(file.get(), path) !=
      ConfigPathObservation::kObservedOnly) return ClientIntegrityObservation::kUnsafePath;
  BY_HANDLE_FILE_INFORMATION before{}, after{};
  if (!GetFileInformationByHandle(file.get(), &before))
    return ClientIntegrityObservation::kUnavailable;
  const auto observed_size = (static_cast<std::uint64_t>(before.nFileSizeHigh) << 32) |
      static_cast<std::uint64_t>(before.nFileSizeLow);
  if (expected_bytes && observed_size != expected_bytes)
    return ClientIntegrityObservation::kSizeMismatch;
  auto unchanged = [&] {
    return GetFileInformationByHandle(file.get(), &after) &&
        client_hash_detail::SameFileInfo(before, after) &&
        config_path_detail::ObserveFileShape(file.get(), path) ==
            ConfigPathObservation::kObservedOnly;
  };
  std::string first, second;
  if (client_hash_detail::HashFixtureHandle(file.get(), &first) !=
      ClientHashObservation::kMatchesOnly) return ClientIntegrityObservation::kHashRejected;
  if (!unchanged()) return ClientIntegrityObservation::kChanged;
  if (!SameClientHash(first, expected_hash)) return ClientIntegrityObservation::kHashMismatch;
  LARGE_INTEGER start{};
  if (!SetFilePointerEx(file.get(), start, nullptr, FILE_BEGIN))
    return ClientIntegrityObservation::kUnavailable;
  // Test injection is only an observation; native wrapper supplies WinVerifyTrust.
  if (signature(file.get(), path) != ClientSignatureObservation::kPolicyMatchOnly)
    return ClientIntegrityObservation::kSignatureRejected;
  if (!unchanged()) return ClientIntegrityObservation::kChanged;
  if (client_hash_detail::HashFixtureHandle(file.get(), &second) !=
      ClientHashObservation::kMatchesOnly || !unchanged() ||
      !SameClientHash(first, second)) return ClientIntegrityObservation::kChanged;
  *observed = std::move(second);
  return ClientIntegrityObservation::kObservationsOnly;
  // Handles release; no signer pin, package trust, process binding or authorization.
}

inline ClientIntegrityObservation ObserveSyntheticClientIntegrity(
    std::wstring_view expected_root, std::wstring_view selected_root,
    std::string_view expected_hash, std::string* observed) {
  return ObserveSyntheticClientIntegrityWith(expected_root, selected_root,
      expected_hash, observed, client_signature_detail::ObserveHandle);
}
}  // namespace veil::chromium_dev
#endif
