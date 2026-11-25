// SPDX-License-Identifier: MPL-2.0
// Narrow synthetic manifest entry contract; no filesystem or package authority.
#ifndef VEIL_CHROMIUM_DEV_MANIFEST_ENTRY_CONTRACT_H_
#define VEIL_CHROMIUM_DEV_MANIFEST_ENTRY_CONTRACT_H_
#include "client-migration-contract.h"
#include <set>

namespace veil::chromium_dev {
inline constexpr std::size_t kManifestEntryLimit = 4u * 1024u * 1024u;
inline constexpr std::size_t kManifestMaxEntries = 4096;
inline constexpr std::string_view kSyntheticClientEntry = "Chromium/client-fixture.exe";
struct ManifestClientEntry {
  std::string source_commit;
  std::string version;
  std::string sha256;
  std::uint64_t bytes = 0;
};
enum class ManifestEntryResult { kRejected, kMalformed, kNoClient, kEntryOnly };

namespace manifest_entry_detail {
using Reader = status_wire_detail::Reader;
inline bool HexCommit(std::string_view value) {
  if (value.size() != 40) return false;
  bool nonzero = false;
  for (auto c : value) {
    if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
    nonzero |= c != '0';
  }
  return nonzero;
}
inline bool Number(std::string_view text, unsigned max, unsigned* value) {
  if (text.empty() || text.size() > 5 || (text.size() > 1 && text.front() == '0'))
    return false;
  unsigned n = 0;
  for (auto c : text) {
    if (c < '0' || c > '9') return false;
    n = n * 10u + static_cast<unsigned>(c - '0');
  }
  if (n > max) return false;
  *value = n;
  return true;
}
inline bool InternalVersion(std::string_view value) {
  unsigned major = 0, part = 0;
  auto dot = value.find('.');
  if (dot == std::string_view::npos || !Number(value.substr(0, dot), 65535, &major))
    return false;
  value.remove_prefix(dot + 1);
  dot = value.find('.');
  if (dot == std::string_view::npos || !Number(value.substr(0, dot), 65535, &part))
    return false;
  value.remove_prefix(dot + 1);
  const auto dash = value.find('-');
  if (!Number(value.substr(0, dash), 65535, &part)) return false;
  if (dash == std::string_view::npos) return major == 0;
  value.remove_prefix(dash + 1);
  dot = value.find('.');
  if (dot == std::string_view::npos) return false;
  const auto stage = value.substr(0, dot);
  return (stage == "alpha" || stage == "beta" || stage == "rc") &&
      Number(value.substr(dot + 1), 99, &part) && part > 0;
}
inline bool SafeRelative(std::string_view path) {
  if (path.size() > 240 || !(path.substr(0, 9) == "Chromium/" ||
                             path.substr(0, 7) == "Broker/")) return false;
  while (true) {
    const auto pos = path.find('/');
    const auto part = path.substr(0, pos);
    std::wstring wide(part.begin(), part.end());
    if (!SafeComponent(wide)) return false;
    if (pos == std::string_view::npos) return true;
    path.remove_prefix(pos + 1);
  }
}
inline std::string FoldPath(std::string value) {
  for (char& c : value)
    if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
  return value;
}
inline bool ReadFiles(Reader& reader, ManifestClientEntry* entry, bool* found) {
  if (!reader.Take('[')) return false;
  std::set<std::string> paths;
  if (reader.Take(']')) return true;
  for (std::size_t count = 0; count < kManifestMaxEntries; ++count) {
    if (count && !reader.Take(',')) return false;
    if (!reader.Take('{')) return false;
    unsigned seen = 0;
    std::string path, hash;
    std::uint64_t bytes = 0;
    for (unsigned i = 0; i < 3; ++i) {
      if (i && !reader.Take(',')) return false;
      std::string key;
      if (!reader.String(&key) || !reader.Take(':')) return false;
      if (key == "path" && !(seen & 1u)) {
        seen |= 1u;
        if (!reader.String(&path) || !SafeRelative(path)) return false;
      } else if (key == "sha256" && !(seen & 2u)) {
        seen |= 2u;
        if (!reader.String(&hash) || !ValidClientHash(hash)) return false;
      } else if (key == "bytes" && !(seen & 4u)) {
        seen |= 4u;
        if (!reader.Unsigned(&bytes)) return false;
      } else return false;
    }
    if (seen != 7 || !reader.Take('}') || !paths.insert(FoldPath(path)).second)
      return false;
    if (path == kSyntheticClientEntry) {
      if (!bytes || bytes > 512ULL * 1024u * 1024u) return false;
      *found = true;
      entry->sha256 = std::move(hash);
      entry->bytes = bytes;
    }
    if (reader.Take(']')) {
      // Reject a file also used as an ancestor directory, regardless of order.
      for (const auto& name : paths) {
        auto pos = name.find('/');
        while (pos != std::string::npos) {
          if (paths.count(name.substr(0, pos))) return false;
          pos = name.find('/', pos + 1);
        }
      }
      return true;
    }
  }
  return false;
}
}  // namespace manifest_entry_detail

inline ManifestEntryResult ParseSyntheticManifestClient(
    std::string_view json, ManifestClientEntry* output) {
  using namespace manifest_entry_detail;
  if (!output || json.empty() || json.size() > kManifestEntryLimit)
    return ManifestEntryResult::kRejected;
  Reader reader(json);
  if (!reader.Take('{')) return ManifestEntryResult::kMalformed;
  constexpr std::array<std::string_view, 7> names{
      "format", "channel", "architecture", "version", "sourceCommit", "profileFormat", "files"};
  unsigned seen = 0;
  bool found = false;
  ManifestClientEntry entry;
  for (std::size_t count = 0; count < names.size(); ++count) {
    if (count && !reader.Take(',')) return ManifestEntryResult::kMalformed;
    std::string key, value;
    if (!reader.String(&key) || !reader.Take(':')) return ManifestEntryResult::kMalformed;
    std::size_t i = 0;
    while (i < names.size() && names[i] != key) ++i;
    if (i == names.size() || (seen & (1u << i))) return ManifestEntryResult::kMalformed;
    seen |= 1u << i;
    switch (i) {
      case 0: case 5: {
        std::uint64_t n = 0;
        if (!reader.Unsigned(&n) || n != 1) return ManifestEntryResult::kMalformed;
        break;
      }
      case 1: case 2:
        if (!reader.String(&value) || value != (i == 1 ? "internal" : "win-x64"))
          return ManifestEntryResult::kMalformed;
        break;
      case 3:
        if (!reader.String(&entry.version) || !InternalVersion(entry.version))
          return ManifestEntryResult::kMalformed;
        break;
      case 4:
        if (!reader.String(&entry.source_commit) || !HexCommit(entry.source_commit))
          return ManifestEntryResult::kMalformed;
        break;
      case 6:
        if (!ReadFiles(reader, &entry, &found)) return ManifestEntryResult::kMalformed;
        break;
      default: return ManifestEntryResult::kMalformed;
    }
  }
  if (seen != 127 || !reader.Take('}') || !reader.End()) return ManifestEntryResult::kMalformed;
  if (!found) return ManifestEntryResult::kNoClient;
  *output = std::move(entry);
  return ManifestEntryResult::kEntryOnly;
}
}  // namespace veil::chromium_dev
#endif
