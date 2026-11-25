// SPDX-License-Identifier: MPL-2.0
// Experimental offline certificate policy observation; not release authority.
#ifndef VEIL_CHROMIUM_DEV_MANIFEST_CHAIN_WIN_H_
#define VEIL_CHROMIUM_DEV_MANIFEST_CHAIN_WIN_H_
#include "manifest-signature-win.h"
#include <cstring>

namespace veil::chromium_dev {
enum class CertificateChainObservation {
  kUnavailable, kWrongUsage, kRevoked, kRevocationUnknown,
  kChainRejected, kPolicyMatchOnly,
};

namespace manifest_chain_detail {
class Chain {
 public:
  PCCERT_CHAIN_CONTEXT value = nullptr;
  Chain() = default;
  ~Chain() { if (value) CertFreeCertificateChain(value); }
  Chain(const Chain&) = delete;
  Chain& operator=(const Chain&) = delete;
};
inline CertificateChainObservation ClassifyErrors(DWORD errors) noexcept {
  if (errors & CERT_TRUST_IS_REVOKED) return CertificateChainObservation::kRevoked;
  if (errors & (CERT_TRUST_REVOCATION_STATUS_UNKNOWN | CERT_TRUST_IS_OFFLINE_REVOCATION))
    return CertificateChainObservation::kRevocationUnknown;
  if (errors) return CertificateChainObservation::kChainRejected;
  return CertificateChainObservation::kPolicyMatchOnly;
}
inline bool ExplicitCodeSigningUsage(PCCERT_CONTEXT certificate) {
  if (!certificate) return false;
  DWORD bytes = 0;
  if (!CertGetEnhancedKeyUsage(certificate, CERT_FIND_EXT_ONLY_ENHKEY_USAGE_FLAG,
      nullptr, &bytes) || bytes < sizeof(CERT_ENHKEY_USAGE) || bytes > 65536)
    return false;
  std::vector<BYTE> storage(bytes);
  auto* usage = reinterpret_cast<PCERT_ENHKEY_USAGE>(storage.data());
  if (!CertGetEnhancedKeyUsage(certificate, CERT_FIND_EXT_ONLY_ENHKEY_USAGE_FLAG,
      usage, &bytes) || !usage->cUsageIdentifier || !usage->rgpszUsageIdentifier)
    return false;
  for (DWORD i = 0; i < usage->cUsageIdentifier; ++i)
    if (usage->rgpszUsageIdentifier[i] &&
        std::strcmp(usage->rgpszUsageIdentifier[i], szOID_PKIX_KP_CODE_SIGNING) == 0)
      return true;
  return false;
}
}  // namespace manifest_chain_detail

inline CertificateChainObservation ObserveOfflineCodeSigningChain(PCCERT_CONTEXT certificate) {
  if (!certificate || !certificate->pCertInfo) return CertificateChainObservation::kUnavailable;
  if (!manifest_chain_detail::ExplicitCodeSigningUsage(certificate))
    return CertificateChainObservation::kWrongUsage;
  char code_signing[] = szOID_PKIX_KP_CODE_SIGNING;
  LPSTR identifiers[] = {code_signing};
  CERT_CHAIN_PARA parameters{};
  parameters.cbSize = sizeof(parameters);
  parameters.RequestedUsage.dwType = USAGE_MATCH_TYPE_AND;
  parameters.RequestedUsage.Usage.cUsageIdentifier = 1;
  parameters.RequestedUsage.Usage.rgpszUsageIdentifier = identifiers;
  manifest_chain_detail::Chain chain;
  // Current time and default Windows trust engine. Never fetch AIA or CRL
  // URLs here; unknown or stale revocation evidence cannot pass.
  constexpr DWORD flags = CERT_CHAIN_CACHE_ONLY_URL_RETRIEVAL |
      CERT_CHAIN_REVOCATION_CHECK_CACHE_ONLY | CERT_CHAIN_DISABLE_AIA |
      CERT_CHAIN_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT;
  if (!CertGetCertificateChain(nullptr, certificate, nullptr,
      certificate->hCertStore, &parameters, flags, nullptr, &chain.value) ||
      !chain.value) return CertificateChainObservation::kUnavailable;
  const auto result = manifest_chain_detail::ClassifyErrors(chain.value->TrustStatus.dwErrorStatus);
  if (result != CertificateChainObservation::kPolicyMatchOnly) return result;
  CERT_CHAIN_POLICY_PARA policy{};
  policy.cbSize = sizeof(policy);
  CERT_CHAIN_POLICY_STATUS status{};
  status.cbSize = sizeof(status);
  if (!CertVerifyCertificateChainPolicy(CERT_CHAIN_POLICY_BASE, chain.value,
      &policy, &status)) return CertificateChainObservation::kUnavailable;
  if (status.dwError != ERROR_SUCCESS) return CertificateChainObservation::kChainRejected;
  return CertificateChainObservation::kPolicyMatchOnly;
}

// Separate stronger fixture entry point. The earlier byte/pin-only API remains
// explicitly non-authoritative, and is not silently reclassified as trusted.
inline ManifestSignatureObservation ObserveSyntheticManifestOfflinePolicy(
    std::string_view manifest, std::string_view cms, std::string_view pin) {
  return ObserveSyntheticManifestSignatureWith(manifest, cms, pin, [](PCCERT_CONTEXT cert) {
    return ObserveOfflineCodeSigningChain(cert) == CertificateChainObservation::kPolicyMatchOnly;
  });
}
}  // namespace veil::chromium_dev
#endif
