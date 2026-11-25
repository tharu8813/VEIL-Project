// SPDX-License-Identifier: MPL-2.0
// Standalone Status-only wire experiment. No transport or authentication.
#ifndef VEIL_CHROMIUM_DEV_BROKER_STATUS_WIRE_H_
#define VEIL_CHROMIUM_DEV_BROKER_STATUS_WIRE_H_
#include "volume-evidence-contract.h"
#include <array>
#include <cstdint>
#include <string>
#include <string_view>

namespace veil::chromium_dev {
inline constexpr std::size_t kBrokerPayloadLimit = 4096;

inline bool EncodeStatusRequest(std::string_view volume_id, std::string* output) {
  // Conservative ASCII identifier subset; do not serialize arbitrary paths,
  // command names, secrets, or unsupported registered identifiers.
  if (!output || volume_id.empty() || volume_id.size() > 64) return false;
  for (char c : volume_id)
    if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
          (c >= '0' && c <= '9') || c == '-' || c == '_')) return false;
  *output = "{\"command\":\"Status\",\"volumeId\":\"" +
            std::string(volume_id) + "\"}\n";
  return true;
}

struct BrokerStatusSnapshot {
  bool attached = false;
  std::wstring volume_guid;
  bool fully_encrypted = false;
  bool protection_on = false;
  bool unlocked = false;
  bool ready_to_unlock = false;
  bool protector_present = false;
};
enum class BrokerStatusResult { kMalformed, kRefused, kSnapshotOnly };

namespace status_wire_detail {
// Deliberately narrow JSON reader for the existing fixed schema. Not a
// general JSON library; non-ASCII and unsupported escapes are rejected.
class Reader {
 public:
  explicit Reader(std::string_view input) : input_(input) {}
  bool Take(char c) {
    Space();
    if (input_.empty() || input_.front() != c) return false;
    input_.remove_prefix(1);
    return true;
  }
  bool Word(std::string_view word) {
    Space();
    if (input_.substr(0, word.size()) != word) return false;
    input_.remove_prefix(word.size());
    return true;
  }
  bool Bool(bool* value) {
    if (Word("true")) { *value = true; return true; }
    if (Word("false")) { *value = false; return true; }
    return false;
  }
  bool String(std::string* value) {
    if (!Take('"')) return false;
    value->clear();
    while (!input_.empty()) {
      char c = input_.front();
      input_.remove_prefix(1);
      if (c == '"') return true;
      if (c == '\\') {
        if (input_.empty()) return false;
        c = input_.front();
        input_.remove_prefix(1);
        if (c == 'u') {
          if (input_.size() < 4) return false;
          unsigned code = 0;
          for (unsigned i = 0; i < 4; ++i) {
            const auto digit = input_[i];
            if (digit >= '0' && digit <= '9') code = code * 16 + unsigned(digit - '0');
            else if (digit >= 'a' && digit <= 'f') code = code * 16 + unsigned(digit - 'a' + 10);
            else if (digit >= 'A' && digit <= 'F') code = code * 16 + unsigned(digit - 'A' + 10);
            else return false;
          }
          input_.remove_prefix(4);
          if (code < 32 || code > 126) return false;
          c = static_cast<char>(code);
        } else if (c != '\\' && c != '"' && c != '/') {
          return false;
        }
      }
      if (static_cast<unsigned char>(c) < 32 || static_cast<unsigned char>(c) > 126)
        return false;
      value->push_back(c);
    }
    return false;
  }
  bool Unsigned(std::uint64_t* value) {
    Space();
    if (input_.empty() || input_.front() < '0' || input_.front() > '9') return false;
    if (input_.front() == '0') {
      input_.remove_prefix(1);
      if (!input_.empty() && input_.front() >= '0' && input_.front() <= '9')
        return false;
      *value = 0;
      return true;
    }
    std::uint64_t result = 0;
    constexpr std::uint64_t limit = 0x7fffffffffffffffULL;
    while (!input_.empty() && input_.front() >= '0' && input_.front() <= '9') {
      const auto digit = static_cast<unsigned>(input_.front() - '0');
      if (result > (limit - digit) / 10u) return false;
      result = result * 10u + digit;
      input_.remove_prefix(1);
    }
    *value = result;
    return true;
  }
  bool End() { Space(); return input_.empty(); }
 private:
  void Space() {
    while (!input_.empty() && (input_.front() == ' ' || input_.front() == '\t' ||
                               input_.front() == '\r' || input_.front() == '\n'))
      input_.remove_prefix(1);
  }
  std::string_view input_;
};

inline bool ReadStatus(Reader& reader, BrokerStatusSnapshot* snapshot) {
  if (!reader.Take('{')) return false;
  constexpr std::array<std::string_view, 7> names{
      "attached", "volumeGuid", "fullyEncrypted", "protectionOn",
      "unlocked", "readyToUnlock", "protectorPresent"};
  unsigned seen = 0;
  bool null_guid = false;
  for (std::size_t count = 0; count < names.size(); ++count) {
    if (count && !reader.Take(',')) return false;
    std::string key;
    if (!reader.String(&key) || !reader.Take(':')) return false;
    std::size_t i = 0;
    while (i < names.size() && names[i] != key) ++i;
    if (i == names.size() || (seen & (1u << i))) return false;
    seen |= 1u << i;
    switch (i) {
      case 0: if (!reader.Bool(&snapshot->attached)) return false; break;
      case 1: {
        if (reader.Word("null")) { null_guid = true; break; }
        std::string guid;
        if (!reader.String(&guid)) return false;
        snapshot->volume_guid.assign(guid.begin(), guid.end());
        if (!IsVolumeGuid(snapshot->volume_guid)) return false;
        break;
      }
      case 2: if (!reader.Bool(&snapshot->fully_encrypted)) return false; break;
      case 3: if (!reader.Bool(&snapshot->protection_on)) return false; break;
      case 4: if (!reader.Bool(&snapshot->unlocked)) return false; break;
      case 5: if (!reader.Bool(&snapshot->ready_to_unlock)) return false; break;
      case 6: if (!reader.Bool(&snapshot->protector_present)) return false; break;
      default: return false;
    }
  }
  if (!reader.Take('}') || seen != 127) return false;
  if (snapshot->attached) return !null_guid;
  return null_guid && !snapshot->fully_encrypted && !snapshot->protection_on &&
      !snapshot->unlocked && !snapshot->ready_to_unlock && !snapshot->protector_present;
}
}  // namespace status_wire_detail

inline BrokerStatusResult DecodeStatusFrame(std::string_view frame,
                                             BrokerStatusSnapshot* output) {
  if (!output || frame.size() < 2 || frame.size() > kBrokerPayloadLimit + 1 ||
      frame.back() != '\n') return BrokerStatusResult::kMalformed;
  frame.remove_suffix(1);
  if (frame.find('\n') != std::string_view::npos) return BrokerStatusResult::kMalformed;
  status_wire_detail::Reader reader(frame);
  if (!reader.Take('{')) return BrokerStatusResult::kMalformed;
  bool ok = false;
  bool seen_ok = false, seen_status = false, seen_error = false;
  BrokerStatusSnapshot snapshot;
  for (unsigned i = 0; i < 2; ++i) {
    if (i && !reader.Take(',')) return BrokerStatusResult::kMalformed;
    std::string key;
    if (!reader.String(&key) || !reader.Take(':')) return BrokerStatusResult::kMalformed;
    if (key == "ok" && !seen_ok) {
      seen_ok = true;
      if (!reader.Bool(&ok)) return BrokerStatusResult::kMalformed;
    } else if (key == "status" && !seen_status) {
      seen_status = true;
      if (!status_wire_detail::ReadStatus(reader, &snapshot)) return BrokerStatusResult::kMalformed;
    } else if (key == "error" && !seen_error) {
      seen_error = true;
      std::string error;
      if (!reader.String(&error) || error.empty()) return BrokerStatusResult::kMalformed;
    } else {
      return BrokerStatusResult::kMalformed;
    }
  }
  if (!reader.Take('}') || !reader.End() || !seen_ok)
    return BrokerStatusResult::kMalformed;
  if (!ok && seen_error && !seen_status) return BrokerStatusResult::kRefused;
  if (!ok || !seen_status || seen_error) return BrokerStatusResult::kMalformed;
  *output = snapshot;
  return BrokerStatusResult::kSnapshotOnly;
}
}  // namespace veil::chromium_dev
#endif
