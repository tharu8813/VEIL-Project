// SPDX-License-Identifier: MPL-2.0
#include "../../chromium/dev/config-path-win.h"
#include <iostream>
using namespace veil::chromium_dev;

int wmain(int argc, wchar_t** argv) {
  if (argc != 2) return 1;
  const std::wstring root(argv[1]);
  constexpr auto filename = L"client-fixture.json";
  const auto file = root + L"\\" + filename;
  const auto link = root + L"\\alias.json";
  unsigned calls = 0;
  auto allow = [&](HANDLE handle, bool directory) {
    ++calls;
    FILE_ATTRIBUTE_TAG_INFO attrs{};
    if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfo, &attrs, sizeof(attrs)) ||
        ((attrs.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0) != directory)
      return ConfigAclObservation::kUnsafeObject;
    return ConfigAclObservation::kObservedOnly; // fixture-only injected positive
  };
  if (ObserveConfigPathWith(root, root, L"client.json", allow) !=
      ConfigPathObservation::kLexicalRejected || calls != 0 ||
      ObserveConfigPathWith(root, root + L"\\other", filename, allow) !=
      ConfigPathObservation::kLexicalRejected || calls != 0) return 2;
  if (ObserveConfigPathWith(root, root, filename, allow) !=
      ConfigPathObservation::kObservedOnly || calls != 3) return 3;
  for (unsigned reject_at = 1; reject_at <= 3; ++reject_at) {
    unsigned seen = 0;
    auto reject = [&](HANDLE, bool) {
      return ++seen == reject_at ? ConfigAclObservation::kUntrustedWrite
                                 : ConfigAclObservation::kObservedOnly;
    };
    if (ObserveConfigPathWith(root, root, filename, reject) !=
        ConfigPathObservation::kAclRejected || seen != reject_at) return 4;
  }
  if (!CreateHardLinkW(link.c_str(), file.c_str(), nullptr)) return 5;
  const auto hardlink = ObserveConfigPathWith(root, root, filename, allow);
  if (!DeleteFileW(link.c_str()) || hardlink != ConfigPathObservation::kMultipleLinks)
    return 6;
  const auto junction = root + L"\\junction";
  if (ObserveConfigPathWith(junction, junction, filename, allow) !=
      ConfigPathObservation::kUnsafePath) return 7;
  const auto missing = root + L"\\missing";
  if (ObserveConfigPathWith(missing, missing, filename, allow) !=
      ConfigPathObservation::kUnsafePath) return 8;
  const auto native = ObserveSyntheticConfigPath(root, root, filename);
  // CI fixture ACL is not necessarily admin-only; no positive native trust claim.
  if (native != ConfigPathObservation::kAclRejected &&
      native != ConfigPathObservation::kObservedOnly) return 9;
  if (!DeleteFileW(file.c_str())) return 10;
  if (ObserveConfigPathWith(root, root, filename, allow) !=
      ConfigPathObservation::kUnavailable) return 11;
  if (!CreateDirectoryW(file.c_str(), nullptr)) return 12;
  const auto wrong_type = ObserveConfigPathWith(root, root, filename, allow);
  if (!RemoveDirectoryW(file.c_str()) || wrong_type != ConfigPathObservation::kUnsafePath)
    return 13;
  std::cout << "PASS: synthetic config parent/file ACL ordering, hardlink, junction, missing and wrong type refusal; no content read or trust approval\n";
}
