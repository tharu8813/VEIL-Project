// SPDX-License-Identifier: MPL-2.0
#include "../../chromium/dev/synthetic-pipe-transport-win.h"
#include <iostream>
#include <thread>
using namespace veil::chromium_dev;
using synthetic_pipe_detail::Handle;

int main() {
  BrokerStatusSnapshot output;
  output.volume_guid = L"unchanged";
  Handle cancel(CreateEventW(nullptr, TRUE, TRUE, nullptr));
  if (!cancel.valid()) return 1;
  if (QuerySyntheticPipeStatus(L"bad\\name", "fixture", 1000, nullptr, &output) !=
      SyntheticPipeResult::kRejected ||
      QuerySyntheticPipeStatus(L"cancelled", "fixture", 1000, cancel.get(), &output) !=
      SyntheticPipeResult::kCancelled ||
      QuerySyntheticPipeStatus(L"absent", "fixture", 1000, nullptr, &output) !=
      SyntheticPipeResult::kUnavailable || output.volume_guid != L"unchanged") return 2;
  const std::string valid = R"({"ok":true,"status":{"attached":false,"volumeGuid":null,"fullyEncrypted":false,"protectionOn":false,"unlocked":false,"readyToUnlock":false,"protectorPresent":false}})" "\n";
  const std::string expected = "{\"command\":\"Status\",\"volumeId\":\"fixture\"}\n";
  for (unsigned scenario = 0; scenario < 5; ++scenario) {
    const std::wstring suffix = std::to_wstring(GetTickCount64()) + L"-" + std::to_wstring(scenario);
    const std::wstring name = L"\\\\.\\pipe\\veil-transport-fixture-" +
        std::to_wstring(GetCurrentProcessId()) + L"-" + suffix;
    Handle server(CreateNamedPipeW(name.c_str(),
        PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
        1, 8192, 8192, 1000, nullptr));
    if (!server.valid()) return 3;
    const std::string reply = scenario == 0 ? valid :
        scenario == 1 ? "{\"ok\":false,\"error\":\"AccessDenied\"}\n" :
        scenario == 2 ? "{}\n" : std::string();
    bool server_ok = false;
    SyntheticPipeResult result;
    output.volume_guid = L"unchanged";
    if (scenario < 3) {
      // Client opens the already-created instance; ConnectNamedPipe handles
      // the documented ERROR_PIPE_CONNECTED race.
      std::thread peer([&] {
        if (!ConnectNamedPipe(server.get(), nullptr) &&
            GetLastError() != ERROR_PIPE_CONNECTED) return;
        std::string request;
        while (request.size() <= expected.size()) {
          char byte = 0;
          DWORD count = 0;
          if (!ReadFile(server.get(), &byte, 1, &count, nullptr) || count != 1) return;
          request.push_back(byte);
          if (byte == '\n') break;
        }
        if (request != expected) return;
        DWORD count = 0;
        server_ok = WriteFile(server.get(), reply.data(), static_cast<DWORD>(reply.size()),
                              &count, nullptr) && count == static_cast<DWORD>(reply.size());
      });
      result = QuerySyntheticPipeStatus(suffix, "fixture", 4000, nullptr, &output);
      // If connect never happened, unblock the fixture peer without waiting
      // for the CI job timeout. The client handle has already been closed.
      if (result == SyntheticPipeResult::kUnavailable ||
          result == SyntheticPipeResult::kRejected)
        CancelSynchronousIo(peer.native_handle());
      peer.join();
      if (!server_ok) return 4;
    } else if (scenario == 3) {
      // No peer reads/writes: the small request fits the pipe buffer.
      result = QuerySyntheticPipeStatus(suffix, "fixture", 150, nullptr, &output);
    } else {
      if (!ResetEvent(cancel.get())) return 5;
      std::thread signal([&] { Sleep(100); SetEvent(cancel.get()); });
      result = QuerySyntheticPipeStatus(suffix, "fixture", 4000, cancel.get(), &output);
      signal.join();
    }
    constexpr SyntheticPipeResult expected_results[] = {
        SyntheticPipeResult::kSnapshotOnly, SyntheticPipeResult::kRefused,
        SyntheticPipeResult::kMalformed, SyntheticPipeResult::kTimeout,
        SyntheticPipeResult::kCancelled};
    if (result != expected_results[scenario]) return 6;
    if (scenario == 0 ? !output.volume_guid.empty()
                      : output.volume_guid != L"unchanged") return 7;
  }
  std::cout << "PASS: same-process synthetic pipe Status, refusal, malformed, timeout, cancellation and absent pipe; no real broker traffic\n";
}
