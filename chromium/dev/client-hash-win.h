// SPDX-License-Identifier: MPL-2.0
// Standalone Windows CNG hash observation, synthetic executable fixture only.
#ifndef VEIL_CHROMIUM_DEV_CLIENT_HASH_WIN_H_
#define VEIL_CHROMIUM_DEV_CLIENT_HASH_WIN_H_
#include "client-migration-contract.h"
#include "config-path-win.h"
#include <bcrypt.h>
#include <vector>

namespace veil::chromium_dev {
enum class ClientHashObservation {
  kRejected, kUnavailable, kUnsafePath, kSizeRejected,
  kReadFailed, kCryptoFailed, kChanged, kMismatch, kMatchesOnly,
};

namespace client_hash_detail {
class Algorithm {
 public:
  BCRYPT_ALG_HANDLE value = nullptr;
  Algorithm() = default;
  ~Algorithm() { if (value) BCryptCloseAlgorithmProvider(value, 0); }
  Algorithm(const Algorithm&) = delete;
  Algorithm& operator=(const Algorithm&) = delete;
};
class Hash {
 public:
  BCRYPT_HASH_HANDLE value = nullptr;
  Hash() = default;
  ~Hash() { if (value) BCryptDestroyHash(value); }
  Hash(const Hash&) = delete;
  Hash& operator=(const Hash&) = delete;
};
inline constexpr LONGLONG kMaxFixtureBytes = 512LL * 1024 * 1024;
inline bool SameFileInfo(const BY_HANDLE_FILE_INFORMATION& a,
                         const BY_HANDLE_FILE_INFORMATION& b) {
  return a.dwVolumeSerialNumber == b.dwVolumeSerialNumber &&
      a.nFileIndexHigh == b.nFileIndexHigh && a.nFileIndexLow == b.nFileIndexLow &&
      a.nFileSizeHigh == b.nFileSizeHigh && a.nFileSizeLow == b.nFileSizeLow &&
      a.dwFileAttributes == b.dwFileAttributes && a.nNumberOfLinks == b.nNumberOfLinks &&
      CompareFileTime(&a.ftLastWriteTime, &b.ftLastWriteTime) == 0;
}

inline ClientHashObservation HashFixtureHandle(HANDLE file, std::string* digest,
                                               bool allow_empty = false) {
  LARGE_INTEGER size{};
  BY_HANDLE_FILE_INFORMATION before{}, after{};
  if (!GetFileSizeEx(file, &size) || !GetFileInformationByHandle(file, &before))
    return ClientHashObservation::kReadFailed;
  if (size.QuadPart < 0 || (!allow_empty && size.QuadPart == 0) ||
      size.QuadPart > kMaxFixtureBytes)
    return ClientHashObservation::kSizeRejected;
  LARGE_INTEGER start{};
  if (!SetFilePointerEx(file, start, nullptr, FILE_BEGIN))
    return ClientHashObservation::kReadFailed;
  Algorithm algorithm;
  if (BCryptOpenAlgorithmProvider(&algorithm.value, BCRYPT_SHA256_ALGORITHM,
                                  nullptr, 0) != 0)
    return ClientHashObservation::kCryptoFailed;
  ULONG object_size = 0, hash_size = 0, count = 0;
  if (BCryptGetProperty(algorithm.value, BCRYPT_OBJECT_LENGTH,
      reinterpret_cast<PUCHAR>(&object_size), sizeof(object_size), &count, 0) != 0 ||
      count != sizeof(object_size) || !object_size || object_size > 1024u * 1024u ||
      BCryptGetProperty(algorithm.value, BCRYPT_HASH_LENGTH,
      reinterpret_cast<PUCHAR>(&hash_size), sizeof(hash_size), &count, 0) != 0 ||
      count != sizeof(hash_size) || hash_size != 32)
    return ClientHashObservation::kCryptoFailed;
  std::vector<UCHAR> object(object_size);
  Hash hash; // destroyed before object and algorithm
  if (BCryptCreateHash(algorithm.value, &hash.value, object.data(),
                       object_size, nullptr, 0, 0) != 0)
    return ClientHashObservation::kCryptoFailed;
  std::array<UCHAR, 65536> buffer{};
  LONGLONG remaining = size.QuadPart;
  while (remaining > 0) {
    const DWORD wanted = remaining < static_cast<LONGLONG>(buffer.size())
        ? static_cast<DWORD>(remaining) : static_cast<DWORD>(buffer.size());
    DWORD read = 0;
    if (!ReadFile(file, buffer.data(), wanted, &read, nullptr))
      return ClientHashObservation::kReadFailed;
    if (!read || read > wanted) return ClientHashObservation::kChanged;
    if (BCryptHashData(hash.value, buffer.data(), read, 0) != 0)
      return ClientHashObservation::kCryptoFailed;
    remaining -= read;
  }
  DWORD extra = 0;
  if (!ReadFile(file, buffer.data(), 1, &extra, nullptr) ||
      !GetFileInformationByHandle(file, &after))
    return ClientHashObservation::kReadFailed;
  if (extra || !SameFileInfo(before, after)) return ClientHashObservation::kChanged;
  std::array<UCHAR, 32> result{};
  if (BCryptFinishHash(hash.value, result.data(), static_cast<ULONG>(result.size()), 0) != 0)
    return ClientHashObservation::kCryptoFailed;
  constexpr char hex[] = "0123456789abcdef";
  std::string value;
  value.reserve(64);
  for (const auto byte : result) {
    value.push_back(hex[byte >> 4]);
    value.push_back(hex[byte & 15]);
  }
  *digest = std::move(value);
  return ClientHashObservation::kMatchesOnly;
}
}  // namespace client_hash_detail

inline ClientHashObservation ObserveSyntheticClientHash(
    std::wstring_view expected_root, std::wstring_view selected_root,
    std::string_view expected_hash, std::string* observed) {
  if (!observed || !ValidClientHash(expected_hash) ||
      Evaluate(expected_root, selected_root, false, false) != PathDecision::kLexicalMatchOnly)
    return ClientHashObservation::kRejected;
  const std::wstring path = std::wstring(selected_root) + L"\\client-fixture.exe";
  if (!SafeDedicatedPath(path)) return ClientHashObservation::kRejected;
  DirectoryHandleRetention retained;
  if (retained.Observe(expected_root, selected_root, false, false) !=
      FilesystemObservation::kObservedOnly) return ClientHashObservation::kUnsafePath;
  config_path_detail::Handle file(CreateFileW(path.c_str(),
      FILE_READ_DATA | FILE_READ_ATTRIBUTES, FILE_SHARE_READ, nullptr,
      OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS |
      FILE_FLAG_SEQUENTIAL_SCAN, nullptr));
  if (!file.valid()) return ClientHashObservation::kUnavailable;
  if (config_path_detail::ObserveFileShape(file.get(), path) !=
      ConfigPathObservation::kObservedOnly) return ClientHashObservation::kUnsafePath;
  std::string candidate;
  const auto result = client_hash_detail::HashFixtureHandle(file.get(), &candidate);
  if (result != ClientHashObservation::kMatchesOnly) return result;
  if (config_path_detail::ObserveFileShape(file.get(), path) !=
      ConfigPathObservation::kObservedOnly) return ClientHashObservation::kChanged;
  if (!SameClientHash(candidate, expected_hash)) return ClientHashObservation::kMismatch;
  *observed = std::move(candidate);
  return ClientHashObservation::kMatchesOnly;
  // No PE/signature/ACL/manifest verification or execution permit.
}
}  // namespace veil::chromium_dev
#endif
