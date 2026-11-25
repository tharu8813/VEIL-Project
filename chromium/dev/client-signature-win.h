// SPDX-License-Identifier: MPL-2.0
// Standalone offline-policy Authenticode observation; fixture only.
#ifndef VEIL_CHROMIUM_DEV_CLIENT_SIGNATURE_WIN_H_
#define VEIL_CHROMIUM_DEV_CLIENT_SIGNATURE_WIN_H_
#include "client-hash-win.h"
#include <wintrust.h>
#include <softpub.h>

namespace veil::chromium_dev {
enum class ClientSignatureObservation {
  kRejected, kUnavailable, kUnsafePath, kSizeRejected,
  kChanged, kNotTrusted, kPolicyMatchOnly,
};

namespace client_signature_detail {
// The Windows provider receives the existing file handle and its exact
// observed path. A provider result is not a package signer pin or authority.
inline ClientSignatureObservation ObserveHandle(HANDLE file,
                                                 const std::wstring& path) noexcept {
  WINTRUST_FILE_INFO info{};
  info.cbStruct = sizeof(info);
  info.pcwszFilePath = path.c_str();
  info.hFile = file;
  WINTRUST_DATA data{};
  data.cbStruct = sizeof(data);
  data.dwUIChoice = WTD_UI_NONE;
  data.fdwRevocationChecks = WTD_REVOKE_NONE;
  data.dwUnionChoice = WTD_CHOICE_FILE;
  data.pFile = &info;
  data.dwStateAction = WTD_STATEACTION_VERIFY;
  // Same offline policy as legacy broker; no live revocation proof.
  data.dwProvFlags = WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL;
  GUID action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
  const LONG status = WinVerifyTrust(nullptr, &action, &data);
  data.dwStateAction = WTD_STATEACTION_CLOSE;
  const LONG closed = WinVerifyTrust(nullptr, &action, &data);
  if (status != ERROR_SUCCESS) return ClientSignatureObservation::kNotTrusted;
  if (closed != ERROR_SUCCESS) return ClientSignatureObservation::kUnavailable;
  return ClientSignatureObservation::kPolicyMatchOnly;
}
}  // namespace client_signature_detail

inline ClientSignatureObservation ObserveSyntheticClientSignature(
    std::wstring_view expected_root, std::wstring_view selected_root) {
  if (Evaluate(expected_root, selected_root, false, false) !=
      PathDecision::kLexicalMatchOnly) return ClientSignatureObservation::kRejected;
  const std::wstring path = std::wstring(selected_root) + L"\\client-fixture.exe";
  if (!SafeDedicatedPath(path)) return ClientSignatureObservation::kRejected;
  DirectoryHandleRetention retained;
  if (retained.Observe(expected_root, selected_root, false, false) !=
      FilesystemObservation::kObservedOnly) return ClientSignatureObservation::kUnsafePath;
  config_path_detail::Handle file(CreateFileW(path.c_str(), GENERIC_READ,
      FILE_SHARE_READ, nullptr, OPEN_EXISTING,
      FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS, nullptr));
  if (!file.valid()) return ClientSignatureObservation::kUnavailable;
  if (config_path_detail::ObserveFileShape(file.get(), path) !=
      ConfigPathObservation::kObservedOnly) return ClientSignatureObservation::kUnsafePath;
  LARGE_INTEGER size{};
  BY_HANDLE_FILE_INFORMATION before{}, after{};
  if (!GetFileSizeEx(file.get(), &size) ||
      !GetFileInformationByHandle(file.get(), &before))
    return ClientSignatureObservation::kUnavailable;
  if (size.QuadPart <= 0 || size.QuadPart > client_hash_detail::kMaxFixtureBytes)
    return ClientSignatureObservation::kSizeRejected;
  const auto result = client_signature_detail::ObserveHandle(file.get(), path);
  if (!GetFileInformationByHandle(file.get(), &after) ||
      !client_hash_detail::SameFileInfo(before, after) ||
      config_path_detail::ObserveFileShape(file.get(), path) !=
          ConfigPathObservation::kObservedOnly) return ClientSignatureObservation::kChanged;
  return result;
  // Not a retained authorization, verified signer identity, or live revocation.
}
}  // namespace veil::chromium_dev
#endif
