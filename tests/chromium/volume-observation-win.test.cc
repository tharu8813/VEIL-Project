// SPDX-License-Identifier: MPL-2.0
#include "../../chromium/dev/volume-observation-win.h"
#include <iostream>
using namespace veil::chromium_dev;

int wmain(int argc, wchar_t** argv) {
  if (argc != 2) return 1;
  constexpr auto selected = L"D:\\veil-dev\\Synthetic_Profile";
  constexpr auto guid = L"\\\\?\\Volume{a1234567-1234-1234-1234-123456789abc}\\";
  const std::wstring volume_path = std::wstring(guid) + L"veil-dev\\Synthetic_Profile";
  std::wstring output = L"unchanged";
  if (ExtractObservedVolume(selected, volume_path, &output) != VolumeObservation::kObservedOnly ||
      output != guid) return 2;
  for (const auto& invalid : {std::wstring(), std::wstring(guid),
          std::wstring(guid) + L"other", std::wstring(selected),
          std::wstring(L"\\\\?\\Volume{00000000-0000-0000-0000-000000000000}\\veil-dev\\Synthetic_Profile")}) {
    output = L"unchanged";
    if (ExtractObservedVolume(selected, invalid, &output) !=
        VolumeObservation::kUnexpectedVolumePath || output != L"unchanged") return 3;
  }
  if (ExtractObservedVolume(L"D:\\..\\unsafe", volume_path, &output) !=
      VolumeObservation::kFilesystemRejected || output != L"unchanged") return 4;
  DirectoryHandleRetention empty;
  std::array<wchar_t, 1024> buffer{};
  if (empty.ReadFinalVolumePath(buffer) != 0) return 5;

  const std::wstring root(argv[1]);
  // Independent mount API comparison on the dedicated synthetic directory.
  std::array<wchar_t, 1024> mount{};
  std::array<wchar_t, 64> expected{};
  if (!GetVolumePathNameW(root.c_str(), mount.data(), static_cast<DWORD>(mount.size())) ||
      !GetVolumeNameForVolumeMountPointW(mount.data(), expected.data(),
                                       static_cast<DWORD>(expected.size()))) return 6;
  if (ObserveExistingVolume(root, root, false, false, &output) !=
      VolumeObservation::kObservedOnly || !IsVolumeGuid(output) ||
      !EqualVolumeAscii(output, expected.data())) return 7;
  const auto nested = root + L"\\nested";
  if (ObserveExistingVolume(nested, nested, false, false, &output) !=
      VolumeObservation::kObservedOnly || !EqualVolumeAscii(output, expected.data())) return 8;
  for (const auto& bad : {root + L"\\missing", root + L"\\file", root + L"\\junction",
                         root + L"\\junction\\nested"}) {
    output = L"unchanged";
    if (ObserveExistingVolume(bad, bad, false, false, &output) !=
        VolumeObservation::kFilesystemRejected || output != L"unchanged") return 9;
  }
  if (ObserveExistingVolume(root, nested, false, false, &output) != VolumeObservation::kFilesystemRejected ||
      ObserveExistingVolume(root, root, true, false, &output) != VolumeObservation::kFilesystemRejected ||
      ObserveExistingVolume(root, root, false, true, &output) != VolumeObservation::kFilesystemRejected ||
      ObserveExistingVolume(root, root, false, false, nullptr) != VolumeObservation::kFilesystemRejected ||
      output != L"unchanged") return 10;
  std::cout << "PASS: retained handle volume GUID agrees with mount API; synthetic rejection and output preservation; no encryption or launch approval\n";
}
