// SPDX-License-Identifier: MPL-2.0
// Development-only same-handle stream and object ACL observation.
#ifndef VEIL_CHROMIUM_DEV_PAYLOAD_OBJECT_POLICY_WIN_H_
#define VEIL_CHROMIUM_DEV_PAYLOAD_OBJECT_POLICY_WIN_H_
#include "config-acl-win.h"
#include <array>
#include <string_view>
#include <cstring>

namespace veil::chromium_dev {
enum class PayloadObjectObservation {
  kUnavailable, kAclRejected, kAlternateStream, kObservedOnly,
};
namespace payload_object_detail {
// FileStreamInfo is obtained on the same open handle, not by a second path
// lookup. Fixed buffer: excessive stream metadata is a refusal, not a retry.
inline PayloadObjectObservation ObserveStreams(HANDLE file, bool directory) {
  alignas(FILE_STREAM_INFO) std::array<unsigned char, 16384> buffer{};
  if (!GetFileInformationByHandleEx(file, FileStreamInfo, buffer.data(),
      static_cast<DWORD>(buffer.size()))) {
    // Windows may report EOF when a directory has no data streams.
    if (directory && GetLastError() == ERROR_HANDLE_EOF)
      return PayloadObjectObservation::kObservedOnly;
    return PayloadObjectObservation::kUnavailable;
  }
  constexpr std::size_t header = offsetof(FILE_STREAM_INFO, StreamName);
  // A successful response must contain exactly the unnamed data stream.
  // A directory with a zero-length name is not accepted as a valid stream.
  const auto* info = reinterpret_cast<const FILE_STREAM_INFO*>(buffer.data());
  if (info->StreamNameLength == 0 || info->StreamNameLength % sizeof(wchar_t) ||
      info->StreamNameLength > buffer.size() - header)
    return PayloadObjectObservation::kUnavailable;
  const std::wstring_view name(info->StreamName,
      info->StreamNameLength / sizeof(wchar_t));
  if (name != L"::$DATA" || info->NextEntryOffset != 0)
    return PayloadObjectObservation::kAlternateStream;
  return PayloadObjectObservation::kObservedOnly;
}
}  // namespace payload_object_detail
inline PayloadObjectObservation ObservePayloadObjectPolicy(HANDLE file, bool directory) {
  if (ObserveConfigObjectHandle(file, directory) != ConfigAclObservation::kObservedOnly)
    return PayloadObjectObservation::kAclRejected;
  return payload_object_detail::ObserveStreams(file, directory);
}
}  // namespace veil::chromium_dev
#endif
