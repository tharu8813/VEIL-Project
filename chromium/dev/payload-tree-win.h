// SPDX-License-Identifier: MPL-2.0
// Read-only bounded synthetic payload tree observations, not package trust.
#ifndef VEIL_CHROMIUM_DEV_PAYLOAD_TREE_WIN_H_
#define VEIL_CHROMIUM_DEV_PAYLOAD_TREE_WIN_H_
#include "manifest-inventory-contract.h"
#include "client-hash-win.h"
#include "payload-object-policy-win.h"

namespace veil::chromium_dev {
enum class PayloadTreeObservation {
  kRejected, kUnavailable, kUnsafePath, kLimit, kChanged, kHashRejected, kObservedOnly, kObjectPolicyRejected,
};

namespace payload_tree_detail {
inline constexpr std::size_t kMaxObjects = 8192;
inline constexpr unsigned kMaxDepth = 16;
inline constexpr std::uint64_t kMaxTotalBytes = 1024ULL * 1024u * 1024u;
class FindHandle {
 public:
  explicit FindHandle(HANDLE handle) : value(handle) {}
  ~FindHandle() { if (value != INVALID_HANDLE_VALUE) FindClose(value); }
  FindHandle(const FindHandle&) = delete;
  FindHandle& operator=(const FindHandle&) = delete;
  HANDLE value;
};
struct Scan {
  bool strict_objects = false;
  std::size_t objects = 0;
  std::uint64_t bytes = 0;
  std::vector<ManifestFileEntry> files;
  std::set<std::string> names;
};
inline PayloadTreeObservation ReadFileEntry(const std::wstring& path,
                                           const std::string& relative, Scan& scan) {
  if (scan.files.size() >= kManifestMaxEntries) return PayloadTreeObservation::kLimit;
  config_path_detail::Handle file(CreateFileW(path.c_str(), GENERIC_READ,
      FILE_SHARE_READ, nullptr, OPEN_EXISTING,
      FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS |
      FILE_FLAG_SEQUENTIAL_SCAN, nullptr));
  if (!file.valid()) return PayloadTreeObservation::kUnavailable;
  if (config_path_detail::ObserveFileShape(file.get(), path) !=
      ConfigPathObservation::kObservedOnly) return PayloadTreeObservation::kUnsafePath;
  if (scan.strict_objects && ObservePayloadObjectPolicy(file.get(), false) !=
      PayloadObjectObservation::kObservedOnly) return PayloadTreeObservation::kObjectPolicyRejected;
  BY_HANDLE_FILE_INFORMATION before{}, after{};
  if (!GetFileInformationByHandle(file.get(), &before)) return PayloadTreeObservation::kUnavailable;
  const auto size = (static_cast<std::uint64_t>(before.nFileSizeHigh) << 32) |
      static_cast<std::uint64_t>(before.nFileSizeLow);
  if (size > static_cast<std::uint64_t>(client_hash_detail::kMaxFixtureBytes) ||
      size > kMaxTotalBytes - scan.bytes) return PayloadTreeObservation::kLimit;
  std::string hash;
  if (client_hash_detail::HashFixtureHandle(file.get(), &hash, true) !=
      ClientHashObservation::kMatchesOnly) return PayloadTreeObservation::kHashRejected;
  if (!GetFileInformationByHandle(file.get(), &after) ||
      !client_hash_detail::SameFileInfo(before, after) ||
      config_path_detail::ObserveFileShape(file.get(), path) !=
          ConfigPathObservation::kObservedOnly) return PayloadTreeObservation::kChanged;
  if (scan.strict_objects && ObservePayloadObjectPolicy(file.get(), false) !=
      PayloadObjectObservation::kObservedOnly) return PayloadTreeObservation::kObjectPolicyRejected;
  scan.bytes += size;
  scan.files.push_back({relative, std::move(hash), size});
  return PayloadTreeObservation::kObservedOnly;
}
inline PayloadTreeObservation Walk(const std::wstring& path, const std::string& relative,
                                   unsigned depth, Scan& scan) {
  if (depth > kMaxDepth) return PayloadTreeObservation::kLimit;
  DirectoryHandleRetention retained;
  if (retained.Observe(path, path, false, false) != FilesystemObservation::kObservedOnly)
    return PayloadTreeObservation::kUnsafePath;
  // Keep the policy-checked directory handle live through enumeration.
  // Existing observation path does not require additional access rights.
  config_path_detail::Handle directory(scan.strict_objects ? CreateFileW(path.c_str(),
      READ_CONTROL | FILE_READ_ATTRIBUTES | FILE_LIST_DIRECTORY,
      FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING,
      FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr) : INVALID_HANDLE_VALUE);
  auto directory_policy = [&] {
    return !scan.strict_objects || (directory.valid() &&
        ValidateDirectoryHandle(directory.get(), path) == FilesystemObservation::kObservedOnly &&
        ObservePayloadObjectPolicy(directory.get(), true) == PayloadObjectObservation::kObservedOnly);
  };
  if (!directory_policy()) return PayloadTreeObservation::kObjectPolicyRejected;
  WIN32_FIND_DATAW data{};
  FindHandle find(FindFirstFileW((path + L"\\*").c_str(), &data));
  if (find.value == INVALID_HANDLE_VALUE) {
    if (GetLastError() == ERROR_FILE_NOT_FOUND &&
        ObserveDirectoryComponent(path) == FilesystemObservation::kObservedOnly)
      return directory_policy() ? PayloadTreeObservation::kObservedOnly
                                : PayloadTreeObservation::kObjectPolicyRejected;
    return PayloadTreeObservation::kUnavailable;
  }
  do {
    const std::wstring_view name(data.cFileName);
    if (name == L"." || name == L"..") continue;
    if (++scan.objects > kMaxObjects) return PayloadTreeObservation::kLimit;
    if (!SafeComponent(name) || (data.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT))
      return PayloadTreeObservation::kUnsafePath;
    std::string narrow;
    for (const auto c : name) narrow.push_back(static_cast<char>(c)); // validated ASCII
    const auto child = path + L"\\" + std::wstring(name);
    const auto rel = relative.empty() ? narrow : relative + "/" + narrow;
    const bool directory = (data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
    if (!SafeDedicatedPath(child) || rel.size() > 240 ||
        !scan.names.insert(manifest_entry_detail::FoldPath(rel)).second)
      return PayloadTreeObservation::kUnsafePath;
    // Payload-only root: no manifest sidecars or other root entries.
    if (relative.empty() && (!directory || (rel != "Chromium" && rel != "Broker")))
      return PayloadTreeObservation::kUnsafePath;
    if (!directory && !manifest_entry_detail::SafeRelative(rel))
      return PayloadTreeObservation::kUnsafePath;
    const auto result = directory ? Walk(child, rel, depth + 1, scan)
                                  : ReadFileEntry(child, rel, scan);
    if (result != PayloadTreeObservation::kObservedOnly) return result;
  } while (FindNextFileW(find.value, &data));
  if (GetLastError() != ERROR_NO_MORE_FILES) return PayloadTreeObservation::kUnavailable;
  if (ObserveDirectoryComponent(path) != FilesystemObservation::kObservedOnly)
    return PayloadTreeObservation::kChanged;
  if (!directory_policy()) return PayloadTreeObservation::kObjectPolicyRejected;
  return PayloadTreeObservation::kObservedOnly;
}
}  // namespace payload_tree_detail

inline PayloadTreeObservation ObserveSyntheticPayloadTree(
    std::wstring_view expected_root, std::wstring_view selected_root,
    std::vector<ManifestFileEntry>* output, bool strict_objects = false) {
  if (!output || Evaluate(expected_root, selected_root, false, false) !=
      PathDecision::kLexicalMatchOnly) return PayloadTreeObservation::kRejected;
  payload_tree_detail::Scan scan;
  scan.strict_objects = strict_objects;
  const auto result = payload_tree_detail::Walk(std::wstring(selected_root), "", 0, scan);
  if (result != PayloadTreeObservation::kObservedOnly) return result;
  *output = std::move(scan.files);
  return PayloadTreeObservation::kObservedOnly;
  // Not an atomic snapshot. Strict object checks are optional and do not prove
  // higher ancestor ACLs. Empty directories do not appear in file inventory.
}
}  // namespace veil::chromium_dev
#endif
