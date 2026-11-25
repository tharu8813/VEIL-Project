// SPDX-License-Identifier: MPL-2.0
// Read-only experimental ACL observation; not trusted configuration loading.
#ifndef VEIL_CHROMIUM_DEV_CONFIG_ACL_WIN_H_
#define VEIL_CHROMIUM_DEV_CONFIG_ACL_WIN_H_
#include <windows.h>
#include <aclapi.h>
#include <sddl.h>
#include <cstddef>

namespace veil::chromium_dev {
enum class ConfigAclObservation {
  kUnavailable, kUnsafeObject, kUntrustedOwner, kUntrustedWrite,
  kUnsupportedAcl, kObservedOnly,
};

namespace config_acl_detail {
class LocalAllocation {
 public:
  explicit LocalAllocation(HLOCAL p) noexcept : p_(p) {}
  ~LocalAllocation() { if (p_) LocalFree(p_); }
  LocalAllocation(const LocalAllocation&) = delete;
  LocalAllocation& operator=(const LocalAllocation&) = delete;
 private:
  HLOCAL p_;
};

inline bool TrustedSid(PSID sid, PSID installer) noexcept {
  return sid && IsValidSid(sid) &&
      (IsWellKnownSid(sid, WinLocalSystemSid) ||
       IsWellKnownSid(sid, WinBuiltinAdministratorsSid) ||
       EqualSid(sid, installer));
}
}  // namespace config_acl_detail

inline ConfigAclObservation ObserveConfigDescriptor(
    PSECURITY_DESCRIPTOR descriptor) noexcept {
  if (!descriptor || !IsValidSecurityDescriptor(descriptor))
    return ConfigAclObservation::kUnavailable;
  PSID installer = nullptr;
  if (!ConvertStringSidToSidW(
      L"S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464",
      &installer)) return ConfigAclObservation::kUnavailable;
  config_acl_detail::LocalAllocation installer_owner(installer);
  PSID owner = nullptr;
  BOOL defaulted = FALSE;
  if (!GetSecurityDescriptorOwner(descriptor, &owner, &defaulted) ||
      !owner || !IsValidSid(owner)) return ConfigAclObservation::kUnavailable;
  if (!config_acl_detail::TrustedSid(owner, installer))
    return ConfigAclObservation::kUntrustedOwner;
  PACL dacl = nullptr;
  BOOL present = FALSE;
  if (!GetSecurityDescriptorDacl(descriptor, &present, &dacl, &defaulted))
    return ConfigAclObservation::kUnavailable;
  if (!present || !dacl || !IsValidAcl(dacl))
    return ConfigAclObservation::kUnsupportedAcl;
  // Mirror existing AdminOnlyFile's change rights, plus generic write/all
  // and security access flags. Do not infer safety from deny ACE ordering.
  constexpr DWORD changes = FILE_WRITE_DATA | FILE_APPEND_DATA |
      FILE_WRITE_EA | FILE_WRITE_ATTRIBUTES | DELETE | FILE_DELETE_CHILD |
      WRITE_DAC | WRITE_OWNER | GENERIC_WRITE | GENERIC_ALL |
      MAXIMUM_ALLOWED | ACCESS_SYSTEM_SECURITY;
  for (DWORD i = 0; i < dacl->AceCount; ++i) {
    void* raw = nullptr;
    if (!GetAce(dacl, i, &raw) || !raw)
      return ConfigAclObservation::kUnsupportedAcl;
    const auto* ace = static_cast<const ACE_HEADER*>(raw);
    if (ace->AceType != ACCESS_ALLOWED_ACE_TYPE &&
        ace->AceType != ACCESS_DENIED_ACE_TYPE)
      return ConfigAclObservation::kUnsupportedAcl;
    // Standard allow and deny ACEs have the same binary layout.
    constexpr std::size_t offset = offsetof(ACCESS_ALLOWED_ACE, SidStart);
    constexpr std::size_t sid_header = offsetof(SID, SubAuthority);
    if (ace->AceSize < offset + sid_header)
      return ConfigAclObservation::kUnsupportedAcl;
    const auto* standard = static_cast<const ACCESS_ALLOWED_ACE*>(raw);
    auto* sid = reinterpret_cast<SID*>(
        static_cast<unsigned char*>(raw) + offset);
    if (sid->SubAuthorityCount > SID_MAX_SUB_AUTHORITIES ||
        ace->AceSize != offset + sid_header +
            static_cast<std::size_t>(sid->SubAuthorityCount) * sizeof(DWORD) ||
        !IsValidSid(sid)) return ConfigAclObservation::kUnsupportedAcl;
    if (ace->AceType == ACCESS_ALLOWED_ACE_TYPE &&
        (standard->Mask & changes) != 0 &&
        !config_acl_detail::TrustedSid(sid, installer))
      return ConfigAclObservation::kUntrustedWrite;
  }
  return ConfigAclObservation::kObservedOnly;
}

inline ConfigAclObservation ObserveConfigObjectHandle(
    HANDLE handle, bool expect_directory) noexcept {
  if (!handle || handle == INVALID_HANDLE_VALUE ||
      GetFileType(handle) != FILE_TYPE_DISK)
    return ConfigAclObservation::kUnavailable;
  FILE_ATTRIBUTE_TAG_INFO attrs{};
  if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfo,
                                   &attrs, sizeof(attrs)))
    return ConfigAclObservation::kUnavailable;
  if ((attrs.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0 ||
      ((attrs.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0) != expect_directory)
    return ConfigAclObservation::kUnsafeObject;
  PSECURITY_DESCRIPTOR descriptor = nullptr;
  const DWORD error = GetSecurityInfo(handle, SE_FILE_OBJECT,
      OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION,
      nullptr, nullptr, nullptr, nullptr, &descriptor);
  config_acl_detail::LocalAllocation owned(descriptor);
  if (error != ERROR_SUCCESS || !descriptor)
    return ConfigAclObservation::kUnavailable;
  return ObserveConfigDescriptor(descriptor);
  // Caller owns handle; no path provenance, parent ACL, content or lease proof.
}
}  // namespace veil::chromium_dev
#endif
