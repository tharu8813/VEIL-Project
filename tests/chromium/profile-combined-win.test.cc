// SPDX-License-Identifier: MPL-2.0
#include "../../chromium/dev/profile-combined-win.h"
#include <iostream>
using namespace veil::chromium_dev;

int wmain(int argc, wchar_t** argv) {
  if (argc != 2) return 1;
  constexpr auto root = L"D:\\veil-dev\\Synthetic_Profile";
  constexpr std::array<PolicyObservation, 3> policies{
      PolicyObservation::kNoValueObserved, PolicyObservation::kOverrideObserved,
      PolicyObservation::kUnknown};
  constexpr std::array<FilesystemObservation, 7> files{
      FilesystemObservation::kLexicalRejected, FilesystemObservation::kNonFixedDrive,
      FilesystemObservation::kUnavailable, FilesystemObservation::kNotDirectory,
      FilesystemObservation::kReparsePoint, FilesystemObservation::kUnexpectedFinalPath,
      FilesystemObservation::kObservedOnly};
  unsigned cases = 0;
  for (auto first : policies) {
    for (auto last : policies) {
      for (auto file : files) {
        unsigned pc = 0, fc = 0;
        bool order = true;
        auto policy = [&] {
          ++pc;
          if (pc == 1) { order &= fc == 0; return first; }
          order &= fc == 1;
          return last;
        };
        auto filesystem = [&](std::wstring_view expected, std::wstring_view selected) {
          ++fc;
          order &= pc == 1 && expected == root && selected == root;
          return file;
        };
        auto expected = CombinedObservation::kObservationsOnly;
        unsigned ep = 2, ef = 1;
        if (first != PolicyObservation::kNoValueObserved) {
          expected = first == PolicyObservation::kOverrideObserved
              ? CombinedObservation::kPolicyOverrideObserved : CombinedObservation::kPolicyUnknown;
          ep = 1; ef = 0;
        } else if (file != FilesystemObservation::kObservedOnly) {
          expected = CombinedObservation::kFilesystemRejected; ep = 1;
        } else if (last != PolicyObservation::kNoValueObserved) {
          expected = last == PolicyObservation::kOverrideObserved
              ? CombinedObservation::kPolicyOverrideObserved : CombinedObservation::kPolicyUnknown;
        }
        if (ObserveCombinedWith(root, root, false, policy, filesystem) != expected ||
            !order || pc != ep || fc != ef) return 2;
        ++cases;
      }
    }
  }
  unsigned calls = 0;
  auto policy = [&] { ++calls; return PolicyObservation::kNoValueObserved; };
  auto filesystem = [&](std::wstring_view, std::wstring_view) {
    ++calls; return FilesystemObservation::kObservedOnly;
  };
  if (ObserveCombinedWith(root, root, true, policy, filesystem) != CombinedObservation::kLexicalRejected ||
      ObserveCombinedWith(root, L"D:\\different", false, policy, filesystem) != CombinedObservation::kLexicalRejected ||
      ObserveCombinedWith(root, L"D:\\..\\unsafe", false, policy, filesystem) != CombinedObservation::kLexicalRejected ||
      calls != 0) return 3;
  // Native Windows filesystem with deterministic fake policy; no registry writes.
  const std::wstring path(argv[1]);
  {
    DirectoryHandleRetention retained;
    auto native_fs = [&](std::wstring_view a, std::wstring_view b) {
      return retained.Observe(a, b, false, false);
    };
    auto absent = [] { return PolicyObservation::kNoValueObserved; };
    if (ObserveCombinedWith(path, path, false, absent, native_fs) !=
        CombinedObservation::kObservationsOnly) return 4;
    const auto missing = path + L"\\missing";
    if (ObserveCombinedWith(missing, missing, false, absent, native_fs) !=
        CombinedObservation::kFilesystemRejected) return 5;
  }
  // Full native wrapper is read-only; runner policy contents are not assumed.
  const auto native = ObserveDevelopmentProfile(path, path, false);
  if (native != CombinedObservation::kObservationsOnly &&
      native != CombinedObservation::kPolicyUnknown &&
      native != CombinedObservation::kPolicyOverrideObserved) return 6;
  if (ObserveDevelopmentProfile(path, path, true) != CombinedObservation::kLexicalRejected)
    return 7;
  std::cout << "PASS: " << cases << " policy/filesystem combinations, lexical early rejection and native synthetic observation; no launch approval\n";
}
