// SPDX-License-Identifier: MPL-2.0
#include "../../chromium/dev/diagnostic-path-contract.h"
#include <iostream>
using namespace veil::chromium_dev;

int main() {
  constexpr auto root = L"D:\\veil-dev\\Synthetic_Profile";
  const struct { DiagnosticKind kind; const wchar_t* suffix; } cases[] = {
      {DiagnosticKind::kCrashDump, L"\\Crashpad"},
      {DiagnosticKind::kWatcher, L"\\Diagnostics"},
      {DiagnosticKind::kLocalTrace, L"\\Local_Traces"},
  };
  std::wstring output;
  for (const auto& entry : cases) {
    if (PlanDiagnosticPath(root, root, entry.kind, false, false, &output) !=
        DiagnosticPlanResult::kLexicalCandidateOnly ||
        output != std::wstring(root) + entry.suffix) return 1;
    for (unsigned flags = 1; flags < 4; ++flags) {
      output = L"unchanged";
      if (PlanDiagnosticPath(root, root, entry.kind, (flags & 1) != 0,
                            (flags & 2) != 0, &output) != DiagnosticPlanResult::kRejected ||
          output != L"unchanged") return 2;
    }
  }
  const auto kind = DiagnosticKind::kCrashDump;
  for (const auto* rejected : {L"D:\\other", L"D:\\..\\unsafe", L"D:\\",
                                L"\\\\server\\share", L"D:\\CON"}) {
    output = L"unchanged";
    if (PlanDiagnosticPath(root, rejected, kind, false, false, &output) !=
        DiagnosticPlanResult::kRejected || output != L"unchanged") return 3;
  }
  output = L"unchanged";
  if (PlanDiagnosticPath(root, root, static_cast<DiagnosticKind>(99), false, false,
                         &output) != DiagnosticPlanResult::kRejected ||
      output != L"unchanged") return 4;
  if (PlanDiagnosticPath(root, root, kind, false, false, nullptr) !=
      DiagnosticPlanResult::kRejected) return 5;
  // Safe input path but appended suffix exceeds the lexical length limit.
  const std::wstring long_root = L"D:\\" + std::wstring(250, L'a');
  if (Evaluate(long_root, long_root, false, false) != PathDecision::kLexicalMatchOnly ||
      PlanDiagnosticPath(long_root, long_root, kind, false, false, &output) !=
          DiagnosticPlanResult::kRejected || output != L"unchanged") return 6;
  std::cout << "PASS: synthetic diagnostics lexical candidates; no IO or runtime approval\n";
}
