// SPDX-License-Identifier: MPL-2.0
#pragma once
#include <string>
#include <string_view>

namespace veil_cef {
inline constexpr std::string_view kVerifiedReply = "{\"ok\":true,\"cefChildVerified\":true}\n";

// Conservative subset of registered volume IDs for this future product
// transport. Never accept a path, escape sequence or injected JSON member.
inline bool BuildRedemptionRequest(std::string_view volume, std::string& request) {
  request.clear();
  if (volume.empty() || volume.size() > 64) return false;
  for (char c : volume) {
    if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
          (c >= '0' && c <= '9') || c == '-' || c == '_')) return false;
  }
  request = "{\"command\":\"RedeemCefChild\",\"volumeId\":\"";
  request.append(volume);
  request += "\"}\n";
  return true;
}

// The broker emits a fixed compact JSON object followed by LF. Fail closed
// on every alternate encoding, extra field, trailing byte, or partial reply.
inline bool IsVerifiedReply(std::string_view reply) {
  return reply == kVerifiedReply;
}

inline constexpr std::string_view kRuntimeAuthorityReply = "{\"ok\":true,\"cefRuntimeAuthorityCurrent\":true}\n";
inline bool BuildRuntimeAuthorityRequest(std::string_view volume, std::string& request) {
  if (!BuildRedemptionRequest(volume, request)) return false;
  request.replace(request.find("RedeemCefChild"), std::string_view("RedeemCefChild").size(),
                  "CheckCefRuntimeAuthority");
  return true;
}
inline bool IsRuntimeAuthorityReply(std::string_view reply) {
  return reply == kRuntimeAuthorityReply;
}

// Disconnected root-only snapshot transport. No receipt from a disabled
// preflight may be used here. No retry, startup or descendant admission.
bool RequestRuntimeAuthority(std::string_view registered_volume);

// Not called by smoke hosts. The caller must supply a trusted registered ID.
// A true return is a verification receipt, NOT permission to initialize CEF.
bool RequestChildVerification(std::string_view registered_volume);
}  // namespace veil_cef
