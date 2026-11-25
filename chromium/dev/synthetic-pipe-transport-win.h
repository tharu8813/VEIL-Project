// SPDX-License-Identifier: MPL-2.0
// Same-process synthetic fixture transport only; never contacts the product broker.
#ifndef VEIL_CHROMIUM_DEV_SYNTHETIC_PIPE_TRANSPORT_WIN_H_
#define VEIL_CHROMIUM_DEV_SYNTHETIC_PIPE_TRANSPORT_WIN_H_
#include "broker-status-wire.h"
#include "broker-identity-win.h"
#include <windows.h>
#include <string>

namespace veil::chromium_dev {
enum class SyntheticPipeResult {
  kRejected, kUnavailable, kTimeout, kCancelled, kIoFailure,
  kMalformed, kRefused, kIdentityRejected, kSnapshotOnly,
};

namespace synthetic_pipe_detail {
class Handle {
 public:
  explicit Handle(HANDLE h) noexcept : h_(h) {}
  ~Handle() { if (h_ && h_ != INVALID_HANDLE_VALUE) CloseHandle(h_); }
  Handle(const Handle&) = delete;
  Handle& operator=(const Handle&) = delete;
  HANDLE get() const noexcept { return h_; }
  bool valid() const noexcept { return h_ && h_ != INVALID_HANDLE_VALUE; }
 private:
  HANDLE h_;
};

inline SyntheticPipeResult CheckWait(ULONGLONG end, HANDLE cancel) noexcept {
  if (cancel) {
    const auto wait = WaitForSingleObject(cancel, 0);
    if (wait == WAIT_OBJECT_0) return SyntheticPipeResult::kCancelled;
    if (wait != WAIT_TIMEOUT) return SyntheticPipeResult::kIoFailure;
  }
  return GetTickCount64() >= end ? SyntheticPipeResult::kTimeout
                                : SyntheticPipeResult::kSnapshotOnly;
}

inline SyntheticPipeResult Transfer(HANDLE pipe, bool writing, void* data,
                                    DWORD size, DWORD* transferred,
                                    ULONGLONG end, HANDLE cancel) noexcept {
  auto state = CheckWait(end, cancel);
  if (state != SyntheticPipeResult::kSnapshotOnly) return state;
  Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
  if (!event.valid()) return SyntheticPipeResult::kIoFailure;
  OVERLAPPED operation{};
  operation.hEvent = event.get();
  *transferred = 0;
  const BOOL done = writing
      ? WriteFile(pipe, data, size, transferred, &operation)
      : ReadFile(pipe, data, size, transferred, &operation);
  if (done) return CheckWait(end, cancel);
  if (GetLastError() != ERROR_IO_PENDING) return SyntheticPipeResult::kIoFailure;
  const auto now = GetTickCount64();
  const DWORD remaining = now >= end ? 0 : static_cast<DWORD>(end - now);
  HANDLE events[] = {event.get(), cancel};
  const DWORD wait = WaitForMultipleObjects(cancel ? 2 : 1, events, FALSE, remaining);
  if (wait == WAIT_OBJECT_0) {
    if (!GetOverlappedResult(pipe, &operation, transferred, FALSE))
      return SyntheticPipeResult::kIoFailure;
    return CheckWait(end, cancel);
  }
  // Drain completion before stack buffers/event/OVERLAPPED are destroyed.
  // Cancellation is a request, not a guarantee of an exact wall-clock limit.
  CancelIoEx(pipe, &operation);
  DWORD ignored = 0;
  GetOverlappedResult(pipe, &operation, &ignored, TRUE);
  if (wait == WAIT_TIMEOUT) return SyntheticPipeResult::kTimeout;
  if (cancel && wait == WAIT_OBJECT_0 + 1) return SyntheticPipeResult::kCancelled;
  return SyntheticPipeResult::kIoFailure;
}
}  // namespace synthetic_pipe_detail

// Injected observer is for fixtures only. It cannot expand the allowed namespace.
template <class IdentityObserver>
SyntheticPipeResult QuerySyntheticPipeStatusWith(
    std::wstring_view suffix, std::string_view volume_id, DWORD timeout_ms,
    HANDLE cancel, BrokerStatusSnapshot* output, IdentityObserver identity) {
  using namespace synthetic_pipe_detail;
  if (!output || suffix.empty() || suffix.size() > 64 ||
      timeout_ms == 0 || timeout_ms > 8000)
    return SyntheticPipeResult::kRejected;
  for (const wchar_t c : suffix)
    if (!((c >= L'a' && c <= L'z') || (c >= L'0' && c <= L'9') || c == L'-'))
      return SyntheticPipeResult::kRejected;
  std::string request;
  if (!EncodeStatusRequest(volume_id, &request)) return SyntheticPipeResult::kRejected;
  const auto end = GetTickCount64() + timeout_ms;
  auto state = CheckWait(end, cancel);
  if (state != SyntheticPipeResult::kSnapshotOnly) return state;
  // No arbitrary pipe names: only this process's fixture namespace.
  const std::wstring name = L"\\\\.\\pipe\\veil-transport-fixture-" +
      std::to_wstring(GetCurrentProcessId()) + L"-" + std::wstring(suffix);
  Handle pipe(CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
      OPEN_EXISTING, FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT |
      SECURITY_IDENTIFICATION, nullptr));
  if (!pipe.valid()) return SyntheticPipeResult::kUnavailable;
  ULONG pid = 0;
  if (!GetNamedPipeServerProcessId(pipe.get(), &pid) || pid != GetCurrentProcessId())
    return SyntheticPipeResult::kRejected;
  if (identity(pipe.get()) != BrokerIdentityObservation::kObservedOnly)
    return SyntheticPipeResult::kIdentityRejected;
  state = CheckWait(end, cancel);
  if (state != SyntheticPipeResult::kSnapshotOnly) return state;
  DWORD mode = PIPE_READMODE_BYTE;
  if (!SetNamedPipeHandleState(pipe.get(), &mode, nullptr, nullptr))
    return SyntheticPipeResult::kIoFailure;
  DWORD written = 0;
  state = Transfer(pipe.get(), true, request.data(),
                   static_cast<DWORD>(request.size()), &written, end, cancel);
  if (state != SyntheticPipeResult::kSnapshotOnly) return state;
  if (written != static_cast<DWORD>(request.size())) return SyntheticPipeResult::kIoFailure;
  std::string response;
  response.reserve(kBrokerPayloadLimit + 1);
  while (response.size() < kBrokerPayloadLimit + 1) {
    char byte = 0;
    DWORD count = 0;
    state = Transfer(pipe.get(), false, &byte, 1, &count, end, cancel);
    if (state != SyntheticPipeResult::kSnapshotOnly) return state;
    if (count != 1) return SyntheticPipeResult::kIoFailure;
    response.push_back(byte);
    if (byte == '\n') {
      BrokerStatusSnapshot snapshot;
      const auto decoded = DecodeStatusFrame(response, &snapshot);
      if (decoded == BrokerStatusResult::kMalformed) return SyntheticPipeResult::kMalformed;
      if (decoded == BrokerStatusResult::kRefused) return SyntheticPipeResult::kRefused;
      state = CheckWait(end, cancel);
      if (state != SyntheticPipeResult::kSnapshotOnly) return state;
      // Recheck the very same open pipe before publishing a successful reply.
      if (!GetNamedPipeServerProcessId(pipe.get(), &pid) ||
          pid != GetCurrentProcessId() ||
          identity(pipe.get()) != BrokerIdentityObservation::kObservedOnly)
        return SyntheticPipeResult::kIdentityRejected;
      state = CheckWait(end, cancel);
      if (state != SyntheticPipeResult::kSnapshotOnly) return state;
      *output = snapshot;
      return SyntheticPipeResult::kSnapshotOnly;
    }
  }
  return SyntheticPipeResult::kMalformed;
}
inline SyntheticPipeResult QuerySyntheticPipeStatus(
    std::wstring_view suffix, std::string_view volume_id, DWORD timeout_ms,
    HANDLE cancel, BrokerStatusSnapshot* output) {
  return QuerySyntheticPipeStatusWith(suffix, volume_id, timeout_ms, cancel,
      output, [](HANDLE pipe) {
        ULONG pid = 0;
        if (!GetNamedPipeServerProcessId(pipe, &pid))
          return BrokerIdentityObservation::kUnavailable;
        return pid == GetCurrentProcessId() ? BrokerIdentityObservation::kObservedOnly
                                            : BrokerIdentityObservation::kMismatch;
      });
}
}  // namespace veil::chromium_dev
#endif
