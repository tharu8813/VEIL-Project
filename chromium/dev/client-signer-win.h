// SPDX-License-Identifier: MPL-2.0
// Synthetic signer certificate SHA-256 pin observation; not release authority.
#ifndef VEIL_CHROMIUM_DEV_CLIENT_SIGNER_WIN_H_
#define VEIL_CHROMIUM_DEV_CLIENT_SIGNER_WIN_H_
#include "client-integrity-win.h"
#include <wincrypt.h>

namespace veil::chromium_dev {
namespace client_signer_detail {
// Extract only the primary signer from the successful verification state,
// not an arbitrary embedded certificate or a timestamp countersigner.
inline bool ReadSignerDigest(HANDLE state, std::array<BYTE, 32>& digest) noexcept {
  if (!state) return false;
  const auto* provider = WTHelperProvDataFromStateData(state);
  if (!provider || provider->csSigners != 1) return false;
  auto* signer = WTHelperGetProvSignerFromChain(
      const_cast<CRYPT_PROVIDER_DATA*>(provider), 0, FALSE, 0);
  if (!signer || !signer->csCertChain || !signer->pasCertChain ||
      signer->dwError != ERROR_SUCCESS) return false;
  const auto* cert = signer->pasCertChain[0].pCert;
  if (!cert || !cert->pbCertEncoded || !cert->cbCertEncoded ||
      cert->cbCertEncoded > 1024u * 1024u) return false;
  DWORD length = static_cast<DWORD>(digest.size());
  return CryptHashCertificate2(BCRYPT_SHA256_ALGORITHM, 0, nullptr,
      cert->pbCertEncoded, cert->cbCertEncoded, digest.data(), &length) &&
      length == static_cast<DWORD>(digest.size());
}
inline ClientSignatureObservation ObservePinnedHandle(
    HANDLE file, const std::wstring& path, std::string_view expected_pin) {
  if (!ValidClientHash(expected_pin)) return ClientSignatureObservation::kRejected;
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
  data.dwProvFlags = WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL;
  GUID action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
  const LONG status = WinVerifyTrust(nullptr, &action, &data);
  std::array<BYTE, 32> digest{};
  const bool extracted = status == ERROR_SUCCESS &&
      ReadSignerDigest(data.hWVTStateData, digest);
  data.dwStateAction = WTD_STATEACTION_CLOSE;
  const LONG closed = WinVerifyTrust(nullptr, &action, &data);
  if (status != ERROR_SUCCESS) return ClientSignatureObservation::kNotTrusted;
  if (!extracted || closed != ERROR_SUCCESS)
    return ClientSignatureObservation::kUnavailable;
  constexpr char hex[] = "0123456789abcdef";
  std::array<char, 64> pin{};
  std::size_t i = 0;
  for (const auto byte : digest) {
    pin[i++] = hex[byte >> 4];
    pin[i++] = hex[byte & 15];
  }
  return SameClientHash(std::string_view(pin.data(), pin.size()), expected_pin)
      ? ClientSignatureObservation::kPolicyMatchOnly
      : ClientSignatureObservation::kNotTrusted;
}
}  // namespace client_signer_detail

inline ClientIntegrityObservation ObserveSyntheticPinnedClient(
    std::wstring_view expected_root, std::wstring_view selected_root,
    std::string_view expected_hash, std::string_view expected_signer_sha256,
    std::string* observed) {
  if (!ValidClientHash(expected_signer_sha256))
    return ClientIntegrityObservation::kRejected;
  return ObserveSyntheticClientIntegrityWith(expected_root, selected_root,
      expected_hash, observed, [&](HANDLE file, const std::wstring& path) {
        return client_signer_detail::ObservePinnedHandle(file, path, expected_signer_sha256);
      });
  // Caller-supplied test pin is not trusted provenance or release approval.
}
}  // namespace veil::chromium_dev
#endif
