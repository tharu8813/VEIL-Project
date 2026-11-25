// SPDX-License-Identifier: MPL-2.0
// Read-only synthetic volume identity observation, not BitLocker verification.
#ifndef VEIL_CHROMIUM_DEV_VOLUME_OBSERVATION_WIN_H_
#define VEIL_CHROMIUM_DEV_VOLUME_OBSERVATION_WIN_H_
#include "profile-filesystem-win.h"
#include "volume-evidence-contract.h"

namespace veil::chromium_dev {
enum class VolumeObservation {
  kFilesystemRejected,
  kUnavailable,
  kUnexpectedVolumePath,
  kObservedOnly,
};

inline VolumeObservation ExtractObservedVolume(
    std::wstring_view selected, std::wstring_view final_volume_path,
    std::wstring* output) {
  if (!output || Evaluate(selected, selected, false, false) !=
                     PathDecision::kLexicalMatchOnly)
    return VolumeObservation::kFilesystemRejected;
  constexpr std::size_t guid_length =
      std::wstring_view(L"\\\\?\\Volume{").size() + 36 + 2;
  if (final_volume_path.size() < guid_length)
    return VolumeObservation::kUnexpectedVolumePath;
  const auto guid = final_volume_path.substr(0, guid_length);
  // Compare the relative name, not the drive letter. Reject unexpected aliases
  // or a volume-relative path that differs from the observed DOS spelling.
  if (!IsVolumeGuid(guid) || final_volume_path.substr(guid_length) != selected.substr(3))
    return VolumeObservation::kUnexpectedVolumePath;
  *output = std::wstring(guid);
  return VolumeObservation::kObservedOnly;
}

inline VolumeObservation ObserveExistingVolume(
    std::wstring_view registered_root, std::wstring_view selected_root,
    bool policy_override_present, bool fallback_requested, std::wstring* output) {
  if (!output) return VolumeObservation::kFilesystemRejected;
  DirectoryHandleRetention retained;
  if (retained.Observe(registered_root, selected_root, policy_override_present,
                      fallback_requested) != FilesystemObservation::kObservedOnly)
    return VolumeObservation::kFilesystemRejected;
  std::array<wchar_t, 1024> buffer{};
  const DWORD count = retained.ReadFinalVolumePath(buffer);
  if (count == 0 || count >= static_cast<DWORD>(buffer.size()))
    return VolumeObservation::kUnavailable;
  return ExtractObservedVolume(selected_root,
                               std::wstring_view(buffer.data(), count), output);
  // All directory handles are released. This is not a live access permit.
}
}  // namespace veil::chromium_dev
#endif
