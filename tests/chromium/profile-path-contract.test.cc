// SPDX-License-Identifier: MPL-2.0
#include "../../chromium/dev/profile-path-contract.h"
#include <cassert>
#include <iostream>
#include <string>
#include <initializer_list>
using veil::chromium_dev::Evaluate;
using veil::chromium_dev::PathDecision;
using veil::chromium_dev::SafeDedicatedPath;
static_assert(SafeDedicatedPath(L"D:\\dev\\synthetic"));
static_assert(!SafeDedicatedPath(L"D:\\dev\\..\\synthetic"));
static_assert(!SafeDedicatedPath(L"D:\\NUL.txt"));
static_assert(Evaluate(L"D:\\dev", L"D:\\dev", false, false) == PathDecision::kLexicalMatchOnly);
int main() {
  constexpr auto root = L"D:\\veil-dev\\Synthetic_Profile";
  assert(Evaluate(root, root, false, false) == PathDecision::kLexicalMatchOnly);
  assert(Evaluate(root, root, true, false) == PathDecision::kRejectedPolicyOverride);
  assert(Evaluate(root, root, false, true) == PathDecision::kRejectedFallback);
  assert(Evaluate(root, L"D:\\Chrome\\UserData", false, false) == PathDecision::kRejectedDifferentRoot);
  assert(Evaluate(root, L"d:\\veil-dev\\Synthetic_Profile", false, false) == PathDecision::kRejectedDifferentRoot);
  for (auto bad : {L"", L"D:", L"D:\\", L"relative", L"D:relative",
                   L"\\\\server\\share", L"\\\\?\\D:\\path", L"D:\\a\\..\\b",
                   L"D:\\a\\.\\b", L"D:\\a\\\\b", L"D:\\a\\", L"D:\\a/b",
                   L"D:\\a:stream", L"D:\\a ", L"D:\\a.", L"D:\\AUX.txt",
                   L"D:\\CoM1", L"D:\\LPT9.txt", L"D:\\a~1", L"D:\\a\\NUL",
                   L"D:\\.hidden", L"D:\\a\u00e9"}) {
    assert(!SafeDedicatedPath(bad));
    assert(Evaluate(root, bad, false, false) == PathDecision::kRejectedUnsafeSyntax);
    assert(Evaluate(bad, bad, false, false) == PathDecision::kRejectedUnsafeSyntax);
  }
  assert(SafeDedicatedPath(L"D:\\veil.dev\\safe-123"));
  const std::wstring max_path = L"D:\\" + std::wstring(256, L'a');
  assert(max_path.size() == 259 && SafeDedicatedPath(max_path));
  assert(!SafeDedicatedPath(max_path + L'a'));
  const wchar_t with_nul[] = L"D:\\dev\0hidden";
  assert(!SafeDedicatedPath(std::wstring_view(with_nul, sizeof(with_nul) / sizeof(wchar_t) - 1)));
  for (auto prefix : {L"COM", L"LPT"}) {
    for (wchar_t digit = L'0'; digit <= L'9'; ++digit) {
      auto device = std::wstring(L"D:\\") + prefix + digit + L".txt";
      assert(!SafeDedicatedPath(device));
    }
  }
  assert(Evaluate(root, root, true, true) == PathDecision::kRejectedPolicyOverride);
  std::cout << "PASS: lexical contract only; filesystem, policy discovery, integration, runtime NOT RUN\n";
}
