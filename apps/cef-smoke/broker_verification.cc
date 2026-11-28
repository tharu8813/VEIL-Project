// SPDX-License-Identifier: MPL-2.0
#include "broker_verification.h"
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <winsvc.h>
#include <array>

namespace veil_cef {
namespace {
class Handle {
 public:
  explicit Handle(HANDLE h) : h_(h) {}
  ~Handle() { if (valid()) CloseHandle(h_); }
  Handle(const Handle&) = delete;
  Handle& operator=(const Handle&) = delete;
  bool valid() const { return h_ && h_ != INVALID_HANDLE_VALUE; }
  HANDLE get() const { return h_; }
 private:
  HANDLE h_;
};
class ServiceHandle {
 public:
  explicit ServiceHandle(SC_HANDLE h) : h_(h) {}
  ~ServiceHandle() { if (h_) CloseServiceHandle(h_); }
  ServiceHandle(const ServiceHandle&) = delete;
  ServiceHandle& operator=(const ServiceHandle&) = delete;
  SC_HANDLE get() const { return h_; }
 private:
  SC_HANDLE h_;
};

constexpr wchar_t kPipe[] = L"\\\\.\\pipe\\VEIL.VolumeBroker";
constexpr ULONGLONG kIoBudgetMs = 8000;
DWORD Remaining(ULONGLONG started) {
  const auto elapsed = GetTickCount64() - started;
  return elapsed >= kIoBudgetMs ? 0 : static_cast<DWORD>(kIoBudgetMs - elapsed);
}

DWORD ServicePid() {
  ServiceHandle manager(OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT));
  if (!manager.get()) return 0;
  ServiceHandle service(OpenServiceW(manager.get(), L"Veil.VolumeBroker", SERVICE_QUERY_STATUS));
  if (!service.get()) return 0;
  SERVICE_STATUS_PROCESS status{};
  DWORD needed = 0;
  if (!QueryServiceStatusEx(service.get(), SC_STATUS_PROCESS_INFO,
      reinterpret_cast<BYTE*>(&status), sizeof(status), &needed) ||
      status.dwCurrentState != SERVICE_RUNNING) return 0;
  return status.dwProcessId;
}

bool SameServer(HANDLE pipe, HANDLE process, DWORD expected) {
  ULONG pipe_pid = 0;
  return expected != 0 && GetNamedPipeServerProcessId(pipe, &pipe_pid) &&
      pipe_pid == expected && GetProcessId(process) == expected &&
      WaitForSingleObject(process, 0) == WAIT_TIMEOUT &&
      ServicePid() == expected;
}

// Cancel pending local pipe I/O on timeout and wait for cancellation to retire
// the OVERLAPPED before releasing its storage. No retry or sync I/O fallback.
bool Transfer(HANDLE pipe, bool write, void* bytes, DWORD size,
              DWORD& count, ULONGLONG started) {
  if (!Remaining(started)) return false;
  Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
  if (!event.valid()) return false;
  OVERLAPPED operation{};
  operation.hEvent = event.get();
  count = 0;
  BOOL done = write ? WriteFile(pipe, bytes, size, nullptr, &operation)
                    : ReadFile(pipe, bytes, size, nullptr, &operation);
  if (!done) {
    if (GetLastError() != ERROR_IO_PENDING) return false;
    if (WaitForSingleObject(event.get(), Remaining(started)) != WAIT_OBJECT_0) {
      CancelIoEx(pipe, &operation);
      GetOverlappedResult(pipe, &operation, &count, TRUE);
      return false;
    }
  }
  return GetOverlappedResult(pipe, &operation, &count, FALSE) != FALSE &&
      Remaining(started) != 0;
}
bool RequestFixedSnapshot(std::string request, bool (*is_reply)(std::string_view)) {
  const ULONGLONG started = GetTickCount64();
  const DWORD pid = ServicePid();
  if (!pid) return false;
  Handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, pid));
  if (!process.valid() || !Remaining(started)) return false;
  // Fixed installed service endpoint. No inherited environment, caller path,
  // alternate pipe name, development endpoint, or retry after a lost reply.
  Handle pipe(CreateFileW(kPipe, GENERIC_READ | GENERIC_WRITE, 0, nullptr,
      OPEN_EXISTING, FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr));
  if (!pipe.valid() || !SameServer(pipe.get(), process.get(), pid)) return false;
  DWORD count = 0;
  if (!Transfer(pipe.get(), true, request.data(), static_cast<DWORD>(request.size()),
      count, started) || count != request.size()) return false;
  std::array<char, 128> buffer{};
  std::string reply;
  while (reply.size() < buffer.size()) {
    char byte = 0;
    if (!Transfer(pipe.get(), false, &byte, 1, count, started) || count != 1) return false;
    reply.push_back(byte);
    if (byte == '\n') break;
  }
  // The protocol is one bounded LF-delimited response. Never consume a
  // second response or treat arbitrary JSON as an authentication result.
  return is_reply(reply) && SameServer(pipe.get(), process.get(), pid) &&
      Remaining(started) != 0;
}
}  // namespace

bool RequestChildVerification(std::string_view registered_volume) {
  std::string request;
  return BuildRedemptionRequest(registered_volume, request) &&
      RequestFixedSnapshot(request, IsVerifiedReply);
}

bool RequestRuntimeAuthority(std::string_view registered_volume) {
  std::string request;
  return BuildRuntimeAuthorityRequest(registered_volume, request) &&
      RequestFixedSnapshot(request, IsRuntimeAuthorityReply);
}
}  // namespace veil_cef
