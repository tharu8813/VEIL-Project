// SPDX-License-Identifier: MPL-2.0
#include "bootstrap_registration.h"
#include <aclapi.h>
#include <utility>

namespace veil_cef {
namespace {
class Key {
 public:
  Key() = default;
  ~Key() { if (key_) RegCloseKey(key_); }
  Key(const Key&) = delete;
  Key& operator=(const Key&) = delete;
  bool Open(HKEY parent, const wchar_t* name) {
    return !key_ && RegOpenKeyExW(parent, name, REG_OPTION_OPEN_LINK,
        KEY_READ | KEY_WOW64_64KEY, &key_) == ERROR_SUCCESS;
  }
  HKEY get() const { return key_; }
 private:
  HKEY key_ = nullptr;
};

bool TrustedSid(PSID sid) {
  return sid && IsValidSid(sid) &&
      (IsWellKnownSid(sid, WinLocalSystemSid) ||
       IsWellKnownSid(sid, WinBuiltinAdministratorsSid));
}

bool IsOrdinaryKey(HKEY key) {
  // Open the link itself, not its target; reject any symbolic-link value.
  return RegQueryValueExW(key, L"SymbolicLinkValue", nullptr, nullptr, nullptr,
                         nullptr) == ERROR_FILE_NOT_FOUND;
}

bool ProtectedKey(HKEY key) {
  PSECURITY_DESCRIPTOR descriptor = nullptr;
  const DWORD status = GetSecurityInfo(key, SE_REGISTRY_KEY,
      OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION,
      nullptr, nullptr, nullptr, nullptr, &descriptor);
  const bool ok = status == ERROR_SUCCESS && descriptor &&
      IsAdminOnlyRegistryDescriptor(descriptor);
  if (descriptor) LocalFree(descriptor);
  return ok && IsOrdinaryKey(key);
}

bool ReadString(HKEY key, const wchar_t* name, std::wstring& result) {
  DWORD type = 0, bytes = 0;
  if (RegQueryValueExW(key, name, nullptr, &type, nullptr, &bytes) != ERROR_SUCCESS ||
      type != REG_SZ || bytes < 2 * sizeof(wchar_t) ||
      bytes > 32768 * sizeof(wchar_t) || bytes % sizeof(wchar_t)) return false;
  std::wstring value(bytes / sizeof(wchar_t), L'\0');
  DWORD actual = bytes;
  if (RegQueryValueExW(key, name, nullptr, &type,
          reinterpret_cast<BYTE*>(value.data()), &actual) != ERROR_SUCCESS ||
      type != REG_SZ || actual != bytes || value.back() != L'\0') return false;
  value.pop_back();
  if (value.find(L'\0') != std::wstring::npos) return false;
  result = std::move(value);
  return true;
}

bool LeafSnapshot(HKEY key, FILETIME& modified) {
  DWORD subkeys = 0, values = 0;
  return RegQueryInfoKeyW(key, nullptr, nullptr, nullptr, &subkeys, nullptr,
      nullptr, &values, nullptr, nullptr, nullptr, &modified) == ERROR_SUCCESS &&
      subkeys == 0 && values == 3;
}

bool Hex(wchar_t c) {
  return (c >= L'0' && c <= L'9') || (c >= L'a' && c <= L'f') ||
      (c >= L'A' && c <= L'F');
}

bool ValidVolumeGuid(const std::wstring& guid) {
  constexpr std::wstring_view prefix = L"\\\\?\\Volume{";
  if (guid.size() != prefix.size() + 38 ||
      !Same(guid.substr(0, prefix.size()), std::wstring(prefix)) ||
      guid.substr(guid.size() - 2) != L"}\\") return false;
  const auto body = std::wstring_view(guid).substr(prefix.size(), 36);
  for (size_t i = 0; i < body.size(); ++i) {
    const bool hyphen = i == 8 || i == 13 || i == 18 || i == 23;
    if (hyphen ? body[i] != L'-' : !Hex(body[i])) return false;
  }
  return true;
}
}  // namespace

bool IsAdminOnlyRegistryDescriptor(PSECURITY_DESCRIPTOR descriptor) {
  if (!descriptor || !IsValidSecurityDescriptor(descriptor)) return false;
  PSID owner = nullptr;
  BOOL defaulted = FALSE, present = FALSE;
  PACL acl = nullptr;
  SECURITY_DESCRIPTOR_CONTROL control{};
  DWORD revision = 0;
  if (!GetSecurityDescriptorOwner(descriptor, &owner, &defaulted) ||
      !TrustedSid(owner) ||
      !GetSecurityDescriptorControl(descriptor, &control, &revision) ||
      !(control & SE_DACL_PROTECTED) ||
      !GetSecurityDescriptorDacl(descriptor, &present, &acl, &defaulted) ||
      !present || !acl || !IsValidAcl(acl)) return false;

  bool trusted_writer = false;
  for (DWORD i = 0; i < acl->AceCount; ++i) {
    void* raw = nullptr;
    if (!GetAce(acl, i, &raw) || !raw) return false;
    const auto* header = static_cast<const ACE_HEADER*>(raw);
    if (header->AceType == ACCESS_DENIED_ACE_TYPE) continue;
    // Do not try to interpret callback/object/conditional allow ACEs.
    if (header->AceType != ACCESS_ALLOWED_ACE_TYPE ||
        header->AceSize < sizeof(ACCESS_ALLOWED_ACE)) return false;
    const auto* ace = static_cast<const ACCESS_ALLOWED_ACE*>(raw);
    PSID sid = const_cast<DWORD*>(&ace->SidStart);
    if (!IsValidSid(sid) ||
        GetLengthSid(sid) > header->AceSize - FIELD_OFFSET(ACCESS_ALLOWED_ACE, SidStart))
      return false;
    if (!TrustedSid(sid)) {
      // Allow only explicit read bits, including for inheritable ACEs.
      // Unknown/generic/maximum-allowed bits fail closed.
      if (ace->Mask & ~static_cast<ACCESS_MASK>(KEY_READ)) return false;
    } else if (!(header->AceFlags & INHERIT_ONLY_ACE) &&
               (ace->Mask & KEY_SET_VALUE)) {
      trusted_writer = true;
    }
  }
  return trusted_writer;
}

bool ParseBootstrapRegistration(const std::wstring& root,
                                const std::wstring& guid,
                                const std::wstring& id,
                                RegisteredBootstrap& result) {
  result = {};
  if (root.size() > 32767 || !DriveAbsolute(fs::path(root)) ||
      !ValidVolumeGuid(guid) || id.empty() || id.size() > 64) return false;
  std::string ascii;
  for (wchar_t c : id) {
    if (c > 127 || c <= 0) return false;
    ascii.push_back(static_cast<char>(c));
  }
  std::string request;
  if (!BuildRedemptionRequest(ascii, request)) return false;
  result = {fs::path(root), guid, ascii};
  return true;
}

bool LoadRegisteredBootstrap(RegisteredBootstrap& result) {
  result = {};
  Key software, veil, leaf;
  // HKLM 64-bit SOFTWARE is the OS-controlled anchor; no HKCU, environment
  // path, registry redirection or caller-provided subkey fallback.
  if (!software.Open(HKEY_LOCAL_MACHINE, L"SOFTWARE") ||
      !IsOrdinaryKey(software.get()) ||
      !veil.Open(software.get(), L"VEIL") || !ProtectedKey(veil.get()) ||
      !leaf.Open(veil.get(), L"CefBootstrap") || !ProtectedKey(leaf.get()))
    return false;
  FILETIME before{}, after{};
  std::wstring root, guid, id;
  if (!LeafSnapshot(leaf.get(), before) ||
      !ReadString(leaf.get(), L"ProductRoot", root) ||
      !ReadString(leaf.get(), L"VolumeGuid", guid) ||
      !ReadString(leaf.get(), L"VolumeId", id) ||
      !LeafSnapshot(leaf.get(), after) || CompareFileTime(&before, &after) != 0 ||
      !ProtectedKey(veil.get()) || !ProtectedKey(leaf.get())) return false;
  return ParseBootstrapRegistration(root, guid, id, result);
}
}  // namespace veil_cef
