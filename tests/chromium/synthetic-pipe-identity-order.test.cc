// SPDX-License-Identifier: MPL-2.0
#include "../../chromium/dev/synthetic-pipe-transport-win.h"
#include <iostream>
#include <thread>
using namespace veil::chromium_dev;
using synthetic_pipe_detail::Handle;

int main() {
  const std::string expected = "{\"command\":\"Status\",\"volumeId\":\"fixture\"}\n";
  const std::string reply = R"({"ok":true,"status":{"attached":false,"volumeGuid":null,"fullyEncrypted":false,"protectionOn":false,"unlocked":false,"readyToUnlock":false,"protectorPresent":false}})" "\n";
  for (unsigned scenario = 0; scenario < 5; ++scenario) {
    const auto suffix = L"identity-" + std::to_wstring(GetTickCount64()) + L"-" +
        std::to_wstring(scenario);
    const std::wstring name = L"\\\\.\\pipe\\veil-transport-fixture-" +
        std::to_wstring(GetCurrentProcessId()) + L"-" + suffix;
    Handle server(CreateNamedPipeW(name.c_str(),
        PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
        1, 8192, 8192, 1000, nullptr));
    Handle connected(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    if (!server.valid() || !connected.valid()) return 1;
    std::string received;
    bool connected_ok = false, reply_sent = false, read_ended = false;
    std::thread peer([&] {
      if (!ConnectNamedPipe(server.get(), nullptr) &&
          GetLastError() != ERROR_PIPE_CONNECTED) return;
      connected_ok = true;
      SetEvent(connected.get());
      while (received.size() <= expected.size()) {
        char byte = 0;
        DWORD count = 0;
        if (!ReadFile(server.get(), &byte, 1, &count, nullptr)) {
          const DWORD error = GetLastError();
          read_ended = error == ERROR_BROKEN_PIPE || error == ERROR_NO_DATA;
          return;
        }
        if (count != 1) return;
        received.push_back(byte);
        if (byte == '\n') break;
      }
      if (received != expected) return;
      DWORD count = 0;
      reply_sent = WriteFile(server.get(), reply.data(), static_cast<DWORD>(reply.size()),
                              &count, nullptr) && count == static_cast<DWORD>(reply.size());
    });
    unsigned calls = 0;
    HANDLE first = nullptr;
    bool same_handle = true;
    auto observe = [&](HANDLE pipe) {
      ++calls;
      if (calls == 1) first = pipe;
      else same_handle = same_handle && first == pipe;
      if (WaitForSingleObject(connected.get(), 1000) != WAIT_OBJECT_0)
        return BrokerIdentityObservation::kUnavailable;
      if (scenario == 0) {
        // Real SCM observer against an unrelated fixture; must refuse before write.
        std::array<wchar_t, 32768> image{};
        DWORD size = static_cast<DWORD>(image.size());
        if (!QueryFullProcessImageNameW(GetCurrentProcess(), 0, image.data(), &size))
          return BrokerIdentityObservation::kUnavailable;
        return ObserveBrokerServiceIdentity(pipe, std::wstring_view(image.data(), size));
      }
      if (scenario == 1) return BrokerIdentityObservation::kUnavailable;
      if (scenario == 2) return BrokerIdentityObservation::kMismatch;
      if (scenario == 3 && calls == 2) return BrokerIdentityObservation::kMismatch;
      return BrokerIdentityObservation::kObservedOnly; // synthetic positive only
    };
    BrokerStatusSnapshot output;
    output.volume_guid = L"unchanged";
    const auto result = QuerySyntheticPipeStatusWith(suffix, "fixture", 4000,
        nullptr, &output, observe);
    if (result == SyntheticPipeResult::kUnavailable ||
        result == SyntheticPipeResult::kRejected)
      CancelSynchronousIo(peer.native_handle());
    peer.join();
    if (!connected_ok || !same_handle) return 2;
    if (scenario < 3) {
      if (result != SyntheticPipeResult::kIdentityRejected || calls != 1 ||
          !received.empty() || reply_sent || !read_ended ||
          output.volume_guid != L"unchanged") return 3;
    } else {
      if (received != expected || !reply_sent || calls != 2) return 4;
      if (scenario == 3) {
        if (result != SyntheticPipeResult::kIdentityRejected ||
            output.volume_guid != L"unchanged") return 5;
      } else if (result != SyntheticPipeResult::kSnapshotOnly ||
                 !output.volume_guid.empty()) return 6;
    }
  }
  std::cout << "PASS: real SCM refusal before fixture write, injected unknown/mismatch, same-handle recheck and snapshot preservation; no real broker traffic\n";
}
