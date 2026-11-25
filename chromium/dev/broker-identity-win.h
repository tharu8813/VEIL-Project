// SPDX-License-Identifier: MPL-2.0
// Read-only development observation. No broker requests or launch approval.
#ifndef VEIL_CHROMIUM_DEV_BROKER_IDENTITY_WIN_H_
#define VEIL_CHROMIUM_DEV_BROKER_IDENTITY_WIN_H_
#include <windows.h>
#include <winsvc.h>
#include <array>
#include <string_view>

namespace veil::chromium_dev {
enum class BrokerIdentityObservation {
  kUnavailable,
  kMismatch,
  kObservedOnly,
};

struct PipeServiceSnapshot {
  DWORD pipe_pid = 0;
  DWORD service_pid = 0;
  bool service_running = false;
};

constexpr BrokerIdentityObservation ComparePipeServiceSnapshots(
    const PipeServiceSnapshot& before, const PipeServiceSnapshot& after,
    bool process_alive, bool image_matches) {
  if (!before.pipe_pid || !before.service_pid ||
      !after.pipe_pid || !after.service_pid)
    return BrokerIdentityObservation::kUnavailable;
  if (!before.service_running || !after.service_running ||
      before.pipe_pid != before.service_pid ||
      after.pipe_pid != after.service_pid ||
      before.pipe_pid != after.pipe_pid || !process_alive || !image_matches)
    return BrokerIdentityObservation::kMismatch;
  return BrokerIdentityObservation::kObservedOnly;
}

namespace broker_identity_detail {
class ServiceHandle {
 public:
  explicit ServiceHandle(SC_HANDLE h) noexcept : h_(h) {}
  ~ServiceHandle() { if (h_) CloseServiceHandle(h_); }
  ServiceHandle(const ServiceHandle&) = delete;
  ServiceHandle& operator=(const ServiceHandle&) = delete;
  SC_HANDLE get() const noexcept { return h_; }
 private:
  SC_HANDLE h_;
};
class ProcessHandle {
 public:
  explicit ProcessHandle(HANDLE h) noexcept : h_(h) {}
  ~ProcessHandle() { if (h_) CloseHandle(h_); }
  ProcessHandle(const ProcessHandle&) = delete;
  ProcessHandle& operator=(const ProcessHandle&) = delete;
  HANDLE get() const noexcept { return h_; }
 private:
  HANDLE h_;
};

inline bool ReadSnapshot(HANDLE pipe, SC_HANDLE service,
                         PipeServiceSnapshot* result) noexcept {
  ULONG pid = 0;
  SERVICE_STATUS_PROCESS status{};
  DWORD needed = 0;
  if (!GetNamedPipeServerProcessId(pipe, &pid) ||
      !QueryServiceStatusEx(service, SC_STATUS_PROCESS_INFO,
          reinterpret_cast<LPBYTE>(&status), sizeof(status), &needed))
    return false;
  *result = {pid, status.dwProcessId, status.dwCurrentState == SERVICE_RUNNING};
  return true;
}
}  // namespace broker_identity_detail

inline BrokerIdentityObservation ObserveBrokerServiceIdentity(
    HANDLE connected_pipe, std::wstring_view expected_image) noexcept {
  // Caller supplies a previously connected local pipe and a separately
  // verified registered image path; neither provenance is established here.
  if (!connected_pipe || connected_pipe == INVALID_HANDLE_VALUE ||
      expected_image.empty() || expected_image.size() > 32767 ||
      expected_image.find(L'\0') != std::wstring_view::npos)
    return BrokerIdentityObservation::kUnavailable;
  using namespace broker_identity_detail;
  ServiceHandle manager(OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT));
  if (!manager.get()) return BrokerIdentityObservation::kUnavailable;
  ServiceHandle service(OpenServiceW(manager.get(), L"Veil.VolumeBroker",
                                    SERVICE_QUERY_STATUS));
  if (!service.get()) return BrokerIdentityObservation::kUnavailable;
  PipeServiceSnapshot before{}, after{};
  if (!ReadSnapshot(connected_pipe, service.get(), &before) ||
      !before.pipe_pid || !before.service_pid)
    return BrokerIdentityObservation::kUnavailable;
  if (!before.service_running || before.pipe_pid != before.service_pid)
    return BrokerIdentityObservation::kMismatch;
  ProcessHandle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE,
                                    FALSE, before.pipe_pid));
  if (!process.get()) return BrokerIdentityObservation::kUnavailable;
  std::array<wchar_t, 32768> image{};
  DWORD length = static_cast<DWORD>(image.size());
  if (!QueryFullProcessImageNameW(process.get(), 0, image.data(), &length) ||
      !length || length >= static_cast<DWORD>(image.size()))
    return BrokerIdentityObservation::kUnavailable;
  const int comparison = CompareStringOrdinal(
      image.data(), static_cast<int>(length), expected_image.data(),
      static_cast<int>(expected_image.size()), TRUE);
  if (!comparison || !ReadSnapshot(connected_pipe, service.get(), &after))
    return BrokerIdentityObservation::kUnavailable;
  const DWORD wait = WaitForSingleObject(process.get(), 0);
  if (wait == WAIT_FAILED) return BrokerIdentityObservation::kUnavailable;
  return ComparePipeServiceSnapshots(before, after, wait == WAIT_TIMEOUT,
                                     comparison == CSTR_EQUAL);
  // No live identity lease, token/ACL proof, binary integrity or transport
  // authorization is returned. Handles are closed before returning.
}
}  // namespace veil::chromium_dev
#endif
