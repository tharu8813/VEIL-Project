// SPDX-License-Identifier: MPL-2.0
#include "../../chromium/dev/profile-filesystem-win.h"
#include <iostream>
using veil::chromium_dev::FilesystemObservation;
using veil::chromium_dev::ObserveExistingDedicatedPath;
using veil::chromium_dev::DirectoryHandleRetention;

// Test DELETE-access sharing conflict only; do not rename or delete directories.
bool CanOpenForDelete(const std::wstring& path, DWORD* error) {
  HANDLE h = CreateFileW(path.c_str(), DELETE,
      FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
      OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
      nullptr);
  if (h == INVALID_HANDLE_VALUE) {
    *error = GetLastError();
    return false;
  }
  CloseHandle(h);
  *error = ERROR_SUCCESS;
  return true;
}
int TestRetention(const std::wstring& path) {
  const auto parent = path.substr(0, path.find_last_of(L'\\'));
  DWORD error = ERROR_SUCCESS;
  if (!CanOpenForDelete(path, &error) || !CanOpenForDelete(parent, &error))
    return 3;
  DirectoryHandleRetention retained;
  if (retained.Observe(path, path, false, false) != FilesystemObservation::kObservedOnly)
    return 4;
  if (CanOpenForDelete(path, &error) || error != ERROR_SHARING_VIOLATION)
    return 5;
  if (CanOpenForDelete(parent, &error) || error != ERROR_SHARING_VIOLATION)
    return 6;
  // A new invalid observation releases previously held handles.
  if (retained.Observe(path, path + L"\\..", false, false) != FilesystemObservation::kLexicalRejected)
    return 7;
  if (!CanOpenForDelete(path, &error) || !CanOpenForDelete(parent, &error))
    return 8;
  if (retained.Observe(path, path, false, false) != FilesystemObservation::kObservedOnly)
    return 9;
  retained.Reset();
  if (!CanOpenForDelete(path, &error) || !CanOpenForDelete(parent, &error))
    return 10;
  std::cout << "PASS: synthetic directory DELETE-access conflicts while handles retained, release verified\n";
  return 0;
}
int wmain(int argc, wchar_t** argv) {
  if (argc != 3) return 2;
  const std::wstring_view path(argv[1]), expected(argv[2]);
  if (expected == L"retention") return TestRetention(std::wstring(path));
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
