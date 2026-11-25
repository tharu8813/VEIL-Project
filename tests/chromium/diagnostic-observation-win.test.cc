// SPDX-License-Identifier: MPL-2.0
#include "../../chromium/dev/diagnostic-observation-win.h"
#include <iostream>
using namespace veil::chromium_dev;

int wmain(int argc, wchar_t** argv) {
  if (argc != 2) return 1;
  const std::wstring root(argv[1]);
  const auto absent = [] { return PolicyObservation::kNoValueObserved; };
  const auto present = [] { return PolicyObservation::kOverrideObserved; };
  const auto unknown = [] { return PolicyObservation::kUnknown; };
  const auto native_fs = [](std::wstring_view a, std::wstring_view b) {
    return ObserveExistingDedicatedPath(a, b, false, false);
  };
  constexpr auto kind = DiagnosticKind::kCrashDump;
  const struct { DiagnosticKind kind; const wchar_t* suffix; } kinds[] = {
      {kind, L"\\Crashpad"},
      {DiagnosticKind::kWatcher, L"\\Diagnostics"},
      {DiagnosticKind::kLocalTrace, L"\\Local_Traces"},
  };
  // Ensure the planner passes the exact full diagnostic child to the observer.
  for (const auto& entry : kinds) {
    const auto child = root + entry.suffix;
    unsigned calls = 0;
    auto check_child = [&](std::wstring_view a, std::wstring_view b) {
      ++calls;
      return a == child && b == child ? FilesystemObservation::kObservedOnly
                                     : FilesystemObservation::kUnexpectedFinalPath;
    };
    if (ObserveDiagnosticWith(root, root, entry.kind, false, absent, check_child) !=
        CombinedObservation::kObservationsOnly || calls != 1) return 2;
    if (ObserveDiagnosticWith(root, root, entry.kind, false, present, check_child) !=
        CombinedObservation::kPolicyOverrideObserved || calls != 1) return 3;
    if (ObserveDiagnosticWith(root, root, entry.kind, false, unknown, check_child) !=
        CombinedObservation::kPolicyUnknown || calls != 1) return 4;
    if (ObserveDiagnosticWith(root, root, entry.kind, true, absent, check_child) !=
        CombinedObservation::kLexicalRejected || calls != 1) return 5;
  }
  // Fixture contains Crashpad directory, Diagnostics file, Local_Traces junction.
  if (ObserveDiagnosticWith(root, root, kind, false, absent, native_fs) !=
      CombinedObservation::kObservationsOnly) return 6;
  if (ObserveDiagnosticWith(root, root, DiagnosticKind::kWatcher, false, absent, native_fs) !=
      CombinedObservation::kFilesystemRejected) return 7;
  if (ObserveDiagnosticWith(root, root, DiagnosticKind::kLocalTrace, false, absent, native_fs) !=
      CombinedObservation::kFilesystemRejected) return 8;
  const auto missing = root + L"\\missing";
  if (ObserveDiagnosticWith(missing, missing, kind, false, absent, native_fs) !=
      CombinedObservation::kFilesystemRejected) return 9;
  if (ObserveDiagnosticWith(root, missing, kind, false, absent, native_fs) !=
      CombinedObservation::kLexicalRejected) return 10;
  unsigned queries = 0;
  auto changed_policy = [&] {
    return ++queries == 1 ? PolicyObservation::kNoValueObserved
                          : PolicyObservation::kOverrideObserved;
  };
  DirectoryHandleRetention retained;
  auto retaining = [&](std::wstring_view a, std::wstring_view b) {
    return retained.Observe(a, b, false, false);
  };
  if (ObserveDiagnosticWith(root, root, kind, false, changed_policy, retaining) !=
      CombinedObservation::kPolicyOverrideObserved || queries != 2) return 11;
  retained.Reset();
  const auto native = ObserveDevelopmentDiagnostic(root, root, kind, false);
  if (native != CombinedObservation::kObservationsOnly &&
      native != CombinedObservation::kPolicyOverrideObserved &&
      native != CombinedObservation::kPolicyUnknown) return 12;
  std::cout << "PASS: diagnostic path child selection, policy rejection, missing/file/junction and existing synthetic directory; no write approval\n";
}
