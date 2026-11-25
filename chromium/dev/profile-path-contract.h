// SPDX-License-Identifier: MPL-2.0
// Standalone development-only lexical contract. Not Chromium runtime code.
// A match is NOT filesystem verification, authorization, or encrypted storage.
#ifndef VEIL_CHROMIUM_DEV_PROFILE_CONTRACT_H_
#define VEIL_CHROMIUM_DEV_PROFILE_CONTRACT_H_
#include <string_view>

namespace veil::chromium_dev {
enum class PathDecision {
  kRejectedUnsafeSyntax,
  kRejectedPolicyOverride,
  kRejectedFallback,
  kRejectedDifferentRoot,
  kLexicalMatchOnly,
};

// Intentionally narrow ASCII syntax for dedicated synthetic test workspaces.
// Reject rather than normalize aliases, short names, streams, or dot segments.
constexpr bool IsLetter(wchar_t c) {
  return (c >= L'A' && c <= L'Z') || (c >= L'a' && c <= L'z');
}
constexpr bool IsDigit(wchar_t c) { return c >= L'0' && c <= L'9'; }
constexpr bool SafeComponent(std::wstring_view value) {
  if (value.empty() || value.back() == L'.') return false;
  for (auto c : value)
    if (!IsLetter(c) && !IsDigit(c) && c != L'-' && c != L'_' && c != L'.')
      return false;
  auto stem = value.substr(0, value.find(L'.'));
  if (stem.empty()) return false;
  wchar_t name[4] = {};
  if (stem.size() <= 4) {
    for (std::size_t i = 0; i < stem.size(); ++i)
      name[i] = stem[i] >= L'a' && stem[i] <= L'z'
                    ? stem[i] - L'a' + L'A' : stem[i];
    std::wstring_view upper(name, stem.size());
    if (upper == L"CON" || upper == L"PRN" || upper == L"AUX" ||
        upper == L"NUL" || upper == L"COM0" || upper == L"LPT0" ||
        (upper.size() == 4 && IsDigit(upper[3]) &&
         (upper.substr(0, 3) == L"COM" || upper.substr(0, 3) == L"LPT")))
      return false;
  }
  return true;
}
constexpr bool SafeDedicatedPath(std::wstring_view path) {
  if (path.size() < 4 || path.size() >= 260 || !IsLetter(path[0]) ||
      path[1] != L':' || path[2] != L'\\') return false;
  auto tail = path.substr(3);
  while (true) {
    auto pos = tail.find(L'\\');
    if (!SafeComponent(tail.substr(0, pos))) return false;
    if (pos == std::wstring_view::npos) return true;
    tail.remove_prefix(pos + 1);
  }
}
constexpr PathDecision Evaluate(std::wstring_view dedicated_root,
                                std::wstring_view selected_path,
                                bool policy_override_present,
                                bool fallback_requested) {
  if (policy_override_present) return PathDecision::kRejectedPolicyOverride;
  if (fallback_requested) return PathDecision::kRejectedFallback;
  if (!SafeDedicatedPath(dedicated_root) || !SafeDedicatedPath(selected_path))
    return PathDecision::kRejectedUnsafeSyntax;
  // Exact spelling only; do not guess at filesystem alias equivalence.
  if (dedicated_root != selected_path) return PathDecision::kRejectedDifferentRoot;
  return PathDecision::kLexicalMatchOnly;
}
}  // namespace veil::chromium_dev
#endif
