// SPDX-License-Identifier: MPL-2.0
// Detached CMS signature observation on synthetic manifest bytes only.
#ifndef VEIL_CHROMIUM_DEV_MANIFEST_SIGNATURE_WIN_H_
#define VEIL_CHROMIUM_DEV_MANIFEST_SIGNATURE_WIN_H_
#include "client-signer-win.h"

namespace veil::chromium_dev {
enum class ManifestSignatureObservation {
  kRejected, kMalformed, kSignatureRejected, kSignerMismatch,
  kSignatureAndPinOnly,
};
inline constexpr std::size_t kManifestFixtureLimit = 4u * 1024u * 1024u;

namespace manifest_signature_detail {
class Certificate {
 public:
  PCCERT_CONTEXT value = nullptr;
  Certificate() = default;
  ~Certificate() { if (value) CertFreeCertificateContext(value); }
  Certificate(const Certificate&) = delete;
  Certificate& operator=(const Certificate&) = delete;
};
}  // namespace manifest_signature_detail

// Cryptographic integrity and exact DER pin only. No chain/EKU/revocation,
// manifest schema, filesystem provenance, package inventory or launch approval.
// A caller-supplied pin must never be treated as trusted provisioning.
inline ManifestSignatureObservation ObserveSyntheticManifestSignature(
    std::string_view manifest, std::string_view detached_cms,
    std::string_view expected_signer_sha256) {
  if (!ValidClientHash(expected_signer_sha256) || manifest.empty() ||
      detached_cms.empty() || manifest.size() > kManifestFixtureLimit ||
      detached_cms.size() > kManifestFixtureLimit)
    return ManifestSignatureObservation::kRejected;
  constexpr DWORD encoding = X509_ASN_ENCODING | PKCS_7_ASN_ENCODING;
  const auto* cms = reinterpret_cast<const BYTE*>(detached_cms.data());
  const auto cms_length = static_cast<DWORD>(detached_cms.size());
  // Exact single signer; negative (malformed) or multiple are not accepted.
  if (CryptGetMessageSignerCount(encoding, cms, cms_length) != 1)
    return ManifestSignatureObservation::kMalformed;
  CRYPT_VERIFY_MESSAGE_PARA params{};
  params.cbSize = sizeof(params);
  params.dwMsgAndCertEncodingType = encoding;
  const BYTE* contents[] = {reinterpret_cast<const BYTE*>(manifest.data())};
  DWORD lengths[] = {static_cast<DWORD>(manifest.size())};
  manifest_signature_detail::Certificate signer;
  if (!CryptVerifyDetachedMessageSignature(&params, 0, cms, cms_length,
                                          1, contents, lengths, &signer.value))
    return ManifestSignatureObservation::kSignatureRejected;
  if (!signer.value || !signer.value->pbCertEncoded || !signer.value->cbCertEncoded ||
      signer.value->cbCertEncoded > 1024u * 1024u)
    return ManifestSignatureObservation::kSignatureRejected;
  std::array<BYTE, 32> digest{};
  DWORD length = static_cast<DWORD>(digest.size());
  if (!CryptHashCertificate2(BCRYPT_SHA256_ALGORITHM, 0, nullptr,
      signer.value->pbCertEncoded, signer.value->cbCertEncoded, digest.data(), &length) ||
      length != static_cast<DWORD>(digest.size()))
    return ManifestSignatureObservation::kSignatureRejected;
  constexpr char hex[] = "0123456789abcdef";
  std::array<char, 64> pin{};
  std::size_t i = 0;
  for (const auto byte : digest) {
    pin[i++] = hex[byte >> 4];
    pin[i++] = hex[byte & 15];
  }
  return SameClientHash(std::string_view(pin.data(), pin.size()), expected_signer_sha256)
      ? ManifestSignatureObservation::kSignatureAndPinOnly
      : ManifestSignatureObservation::kSignerMismatch;
}
}  // namespace veil::chromium_dev
#endif
