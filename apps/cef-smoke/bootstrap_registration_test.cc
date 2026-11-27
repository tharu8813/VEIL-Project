// SPDX-License-Identifier: MPL-2.0
#include "bootstrap_registration.h"
#include <sddl.h>
#include <iostream>

namespace {
bool Accept(const wchar_t* sddl) {
  PSECURITY_DESCRIPTOR descriptor = nullptr;
  if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, SDDL_REVISION_1,
      &descriptor, nullptr)) return false;
  const bool result = veil_cef::IsAdminOnlyRegistryDescriptor(descriptor);
  LocalFree(descriptor);
  return result;
}
}

int main() {
  using namespace veil_cef;
  // In-memory security descriptors only: never open or modify installed HKLM.
  if (!Accept(L"O:BAD:P(A;;KA;;;SY)(A;;KA;;;BA)(A;;KR;;;BU)")) return 1;
  if (Accept(L"O:BUD:P(A;;KA;;;BA)(A;;KR;;;BU)")) return 2;
  if (Accept(L"O:BAD:P(A;;KA;;;BA)(A;;KW;;;BU)")) return 3;
  if (Accept(L"O:BAD:(A;;KA;;;BA)(A;;KR;;;BU)")) return 4;
  if (Accept(L"O:BAD:P(A;;KA;;;BA)(A;;WD;;;BU)")) return 5;
  if (Accept(L"O:BAD:P(A;CIIO;KW;;;BU)(A;;KA;;;BA)")) return 6;
  if (Accept(L"O:BAD:P(A;;KR;;;BU)")) return 7;
  SECURITY_DESCRIPTOR null_dacl{};
  if (!InitializeSecurityDescriptor(&null_dacl, SECURITY_DESCRIPTOR_REVISION) ||
      !SetSecurityDescriptorDacl(&null_dacl, TRUE, nullptr, FALSE) ||
      IsAdminOnlyRegistryDescriptor(&null_dacl)) return 8;

  RegisteredBootstrap registration;
  const std::wstring root = L"V:\\VEIL\\profile";
  const std::wstring guid = L"\\\\?\\Volume{01234567-89ab-cdef-0123-456789abcdef}\\";
  if (!ParseBootstrapRegistration(root, guid, L"volume_01", registration) ||
      registration.volume_id != "volume_01" || registration.product_root != root) return 9;
  if (ParseBootstrapRegistration(L"relative", guid, L"volume_01", registration) ||
      !registration.product_root.empty()) return 10;
  if (ParseBootstrapRegistration(root, guid + L"x", L"volume_01", registration)) return 11;
  if (ParseBootstrapRegistration(root, guid, L"bad/id", registration)) return 12;
  if (ParseBootstrapRegistration(root, guid, std::wstring(L"good\0bad", 8), registration)) return 13;
  if (ParseBootstrapRegistration(root, L"\\\\?\\Volume{01234567-89ab-cdef-0123-456789abcdeg}\\",
      L"volume_01", registration)) return 14;
  std::cout << "PASS: synthetic native registration ACL and format checks\n";
  return 0;
}
