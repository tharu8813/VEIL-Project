// SPDX-License-Identifier: MPL-2.0
#include "../../chromium/dev/config-acl-win.h"
#include <iostream>
#include <string>
using namespace veil::chromium_dev;

int wmain(int argc, wchar_t** argv) {
  if (argc != 2) return 1;
  struct Case { const wchar_t* sddl; ConfigAclObservation expected; };
  const Case cases[] = {
    {L"O:SYD:(A;;FA;;;SY)(A;;FR;;;BU)", ConfigAclObservation::kObservedOnly},
    {L"O:BAD:(A;;FA;;;BA)(A;;FR;;;WD)", ConfigAclObservation::kObservedOnly},
    {L"O:S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464D:(A;;FA;;;SY)", ConfigAclObservation::kObservedOnly},
    {L"O:WDD:(A;;FR;;;WD)", ConfigAclObservation::kUntrustedOwner},
    {L"O:SYD:(A;;FW;;;BU)", ConfigAclObservation::kUntrustedWrite},
    {L"O:SYD:(A;;GW;;;WD)", ConfigAclObservation::kUntrustedWrite},
    {L"O:SYD:(A;;GA;;;WD)", ConfigAclObservation::kUntrustedWrite},
    {L"O:SYD:(A;;WD;;;BU)", ConfigAclObservation::kUntrustedWrite},
    {L"O:SYD:(A;;WO;;;BU)", ConfigAclObservation::kUntrustedWrite},
    {L"O:SYD:(A;;SD;;;BU)", ConfigAclObservation::kUntrustedWrite},
    {L"O:SYD:(A;;DC;;;BU)", ConfigAclObservation::kUntrustedWrite},
    {L"O:SYD:(D;;FW;;;BU)(A;;FW;;;BU)", ConfigAclObservation::kUntrustedWrite},
    {L"O:SYD:(A;OICIIO;FW;;;BU)", ConfigAclObservation::kUntrustedWrite},
    {L"O:SY", ConfigAclObservation::kUnsupportedAcl},
    {L"O:SYD:", ConfigAclObservation::kObservedOnly},
  };
  for (const auto& entry : cases) {
    PSECURITY_DESCRIPTOR sd = nullptr;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
        entry.sddl, SDDL_REVISION_1, &sd, nullptr)) return 2;
    config_acl_detail::LocalAllocation allocation(sd);
    if (ObserveConfigDescriptor(sd) != entry.expected) return 3;
  }
  SECURITY_DESCRIPTOR null_dacl{};
  PSID system = nullptr;
  if (!ConvertStringSidToSidW(L"S-1-5-18", &system)) return 4;
  config_acl_detail::LocalAllocation system_owner(system);
  if (!InitializeSecurityDescriptor(&null_dacl, SECURITY_DESCRIPTOR_REVISION) ||
      !SetSecurityDescriptorOwner(&null_dacl, system, FALSE) ||
      !SetSecurityDescriptorDacl(&null_dacl, TRUE, nullptr, FALSE) ||
      ObserveConfigDescriptor(&null_dacl) != ConfigAclObservation::kUnsupportedAcl)
    return 5;
  if (ObserveConfigObjectHandle(INVALID_HANDLE_VALUE, false) !=
      ConfigAclObservation::kUnavailable) return 6;
  // Native file and directory observation on synthetic fixture only.
  const std::wstring root(argv[1]);
  HANDLE directory = CreateFileW(root.c_str(), READ_CONTROL | FILE_READ_ATTRIBUTES,
      FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING,
      FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
  if (directory == INVALID_HANDLE_VALUE) return 7;
  const auto directory_result = ObserveConfigObjectHandle(directory, true);
  const auto wrong_type = ObserveConfigObjectHandle(directory, false);
  CloseHandle(directory);
  if (directory_result == ConfigAclObservation::kUnavailable ||
      directory_result == ConfigAclObservation::kUnsafeObject ||
      wrong_type != ConfigAclObservation::kUnsafeObject) return 8;
  // Only an empty synthetic file, explicit untrusted writable DACL.
  PSECURITY_DESCRIPTOR sd = nullptr;
  if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
      L"D:P(A;;GA;;;WD)", SDDL_REVISION_1, &sd, nullptr)) return 9;
  config_acl_detail::LocalAllocation allocation(sd);
  SECURITY_ATTRIBUTES sa{sizeof(SECURITY_ATTRIBUTES), sd, FALSE};
  const auto path = root + L"\\untrusted-fixture.json";
  HANDLE file = CreateFileW(path.c_str(), READ_CONTROL | FILE_READ_ATTRIBUTES,
      FILE_SHARE_READ, &sa, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
  if (file == INVALID_HANDLE_VALUE) return 10;
  const auto result = ObserveConfigObjectHandle(file, false);
  const auto directory_type = ObserveConfigObjectHandle(file, true);
  CloseHandle(file);
  if (!DeleteFileW(path.c_str())) return 11;
  if ((result != ConfigAclObservation::kUntrustedOwner &&
       result != ConfigAclObservation::kUntrustedWrite) ||
      directory_type != ConfigAclObservation::kUnsafeObject) return 12;
  std::cout << "PASS: synthetic owner/DACL cases, null DACL rejection, native writable fixture refusal and object type; no configuration content read\n";
}
