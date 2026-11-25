// SPDX-License-Identifier: MPL-2.0
#include "../../chromium/dev/profile-filesystem-win.h"
#include <iostream>
using veil::chromium_dev::FilesystemObservation;
using veil::chromium_dev::ObserveExistingDedicatedPath;
int wmain(int argc, wchar_t** argv) {
  if (argc != 3) return 2;
  const std::wstring_view path(argv[1]), expected(argv[2]);
  const auto observed = ObserveExistingDedicatedPath(path, path, false, false);
  const bool ok =
      (expected == L"ordinary" && observed == FilesystemObservation::kObservedOnly) ||
      (expected == L"missing" && observed == FilesystemObservation::kUnavailable) ||
      (expected == L"file" && observed == FilesystemObservation::kNotDirectory) ||
      (expected == L"junction" && observed == FilesystemObservation::kReparsePoint) ||
      (expected == L"syntax" && observed == FilesystemObservation::kLexicalRejected);
  std::cout << "Expected fixture observation, actual enum: "
            << static_cast<int>(observed) << '\n';
  return ok ? 0 : 1;
}
