// SPDX-License-Identifier: MPL-2.0
#include "broker_verification.h"
#include <iostream>
#include <initializer_list>

int main() {
  using namespace veil_cef;
  std::string request = "old";
  if (!BuildRedemptionRequest("volume_01-A", request) ||
      request != "{\"command\":\"RedeemCefChild\",\"volumeId\":\"volume_01-A\"}\n") return 1;
  for (const std::string& bad : std::initializer_list<std::string>{"", "a/b", "a\\b", "a\"b", "a\nb", "a b", "a.b",
                          std::string("a\0b", 3), std::string(65, 'a')}) {
    if (BuildRedemptionRequest(bad, request) || !request.empty()) return 2;
  }
  if (!IsVerifiedReply(kVerifiedReply)) return 3;
  for (std::string_view bad : {"", "{\"ok\":true}\n",
      "{\"ok\":true,\"cefChildVerified\":false}\n",
      "{\"ok\":false,\"cefChildVerified\":true}\n",
      "{\"ok\":true,\"cefChildVerified\":true}",
      "{\"ok\":true,\"cefChildVerified\":true,\"extra\":1}\n",
      "{\"ok\":true,\"cefChildVerified\":true}\r\n",
      "{\"ok\":true,\"cefChildVerified\":true}\nextra",
      "{\"ok\":true,\"ok\":false,\"cefChildVerified\":true}\n"}) {
    if (IsVerifiedReply(bad)) return 4;
  }
  // Invalid IDs must fail before attempting any SCM or pipe operation.
  if (RequestChildVerification("invalid/volume")) return 5;
  if (!BuildRuntimeAuthorityRequest("volume_01-A", request) ||
      request != "{\"command\":\"CheckCefRuntimeAuthority\",\"volumeId\":\"volume_01-A\"}\n") return 6;
  RuntimeAuthoritySession invalid("bad/volume");
  if (BuildRuntimeAuthorityRequest("bad/volume", request) || !request.empty() ||
      invalid.Check() || invalid.Check()) return 7;
  if (!IsRuntimeAuthorityReply(kRuntimeAuthorityReply) ||
      IsRuntimeAuthorityReply(kVerifiedReply) || IsVerifiedReply(kRuntimeAuthorityReply)) return 8;
  for (std::string_view bad : {"", "{\"ok\":true}\n",
      "{\"ok\":true,\"cefRuntimeAuthorityCurrent\":false}\n",
      "{\"ok\":true,\"cefRuntimeAuthorityCurrent\":true}\r\n",
      "{\"ok\":true,\"cefRuntimeAuthorityCurrent\":true,\"extra\":1}\n"})
    if (IsRuntimeAuthorityReply(bad)) return 9;
  RuntimeAuthorityLatch latch;
  int samples = 0;
  if (!latch.Check([&] { ++samples; return true; }) ||
      latch.Check([&] { ++samples; return false; }) ||
      latch.Check([&] { ++samples; return true; }) || samples != 2) return 10;
  RuntimeAuthorityLatch recursive;
  if (recursive.Check([&] {
        if (recursive.Check([] { return true; })) return false;
        return true;
      }) || recursive.Check([] { return true; })) return 11;
  RuntimeAuthorityLatch throwing;
  if (throwing.Check([]() -> bool { throw 1; }) ||
      throwing.Check([] { return true; })) return 12;
  std::cout << "PASS: native CEF redemption, runtime wire and sticky sample refusal\n";
  return 0;
}
