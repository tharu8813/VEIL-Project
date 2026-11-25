// SPDX-License-Identifier: MPL-2.0
#include "../../chromium/dev/broker-identity-win.h"
#include <iostream>
#include <string>
using namespace veil::chromium_dev;

int main() {
  unsigned accepted = 0;
  unsigned cases = 0;
  for (DWORD a = 0; a < 3; ++a)
    for (DWORD b = 0; b < 3; ++b)
      for (DWORD c = 0; c < 3; ++c)
        for (DWORD d = 0; d < 3; ++d)
          for (unsigned flags = 0; flags < 16; ++flags) {
            const PipeServiceSnapshot before{a, b, (flags & 1u) != 0};
            const PipeServiceSnapshot after{c, d, (flags & 2u) != 0};
            const auto got = ComparePipeServiceSnapshots(before, after,
                (flags & 4u) != 0, (flags & 8u) != 0);
            const auto expected = !a || !b || !c || !d
                ? BrokerIdentityObservation::kUnavailable
                : a == b && b == c && c == d && flags == 15
                    ? BrokerIdentityObservation::kObservedOnly
                    : BrokerIdentityObservation::kMismatch;
            if (got != expected) return 1;
            if (got == BrokerIdentityObservation::kObservedOnly) ++accepted;
            ++cases;
          }
  if (accepted != 2 || cases != 1296) return 2;
  if (ObserveBrokerServiceIdentity(INVALID_HANDLE_VALUE, L"synthetic") !=
      BrokerIdentityObservation::kUnavailable) return 3;
  // Create only our own unique, local synthetic pipe. No broker traffic.
  const std::wstring name = L"\\\\.\\pipe\\veil-identity-fixture-" +
      std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64());
  HANDLE server = CreateNamedPipeW(name.c_str(),
      PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE,
      PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
      1, 512, 512, 1000, nullptr);
  if (server == INVALID_HANDLE_VALUE) return 4;
  HANDLE client = CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0,
      nullptr, OPEN_EXISTING, SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr);
  if (client == INVALID_HANDLE_VALUE) { CloseHandle(server); return 5; }
  ULONG pid = 0;
  const bool pid_matches = GetNamedPipeServerProcessId(client, &pid) &&
      pid == GetCurrentProcessId();
  std::array<wchar_t, 32768> image{};
  DWORD length = static_cast<DWORD>(image.size());
  const bool image_read = QueryFullProcessImageNameW(GetCurrentProcess(), 0,
      image.data(), &length) && length && length < static_cast<DWORD>(image.size());
  const auto observation = image_read
      ? ObserveBrokerServiceIdentity(client, std::wstring_view(image.data(), length))
      : BrokerIdentityObservation::kObservedOnly;
  CloseHandle(client);
  CloseHandle(server);
  if (!pid_matches || !image_read || observation == BrokerIdentityObservation::kObservedOnly)
    return 6;
  std::cout << "PASS: 1296 synthetic PID/service/image cases and native unrelated local pipe refusal; no broker traffic or authentication approval\n";
}
