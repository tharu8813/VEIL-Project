// SPDX-License-Identifier: MPL-2.0
// Development-only, read-only observation of a proposed Windows policy namespace.
// No policy modification, no profile access, no execution authorization.
#ifndef VEIL_CHROMIUM_DEV_PROFILE_POLICY_WIN_H_
#define VEIL_CHROMIUM_DEV_PROFILE_POLICY_WIN_H_
#include <windows.h>
#include <array>

namespace veil::chromium_dev {
enum class PolicyValueObservation { kAbsent, kPresent, kUnknown };
enum class PolicyObservation { kNoValueObserved, kOverrideObserved, kUnknown };

// Proposed development namespace from the unregistered identity candidate.
// This is deliberately NOT a query of existing Chrome/Chromium/legacy VEIL keys.
inline constexpr wchar_t kDevPolicyKey[] = L"SOFTWARE\\Policies\\VEIL Chromium Dev";
inline constexpr wchar_t kProfileValue[] = L"UserDataDir";

inline PolicyValueObservation ClassifyOpenResult(LSTATUS status) {
  if (status == ERROR_SUCCESS) return PolicyValueObservation::kPresent;
  if (status == ERROR_FILE_NOT_FOUND || status == ERROR_PATH_NOT_FOUND)
    return PolicyValueObservation::kAbsent;
  return PolicyValueObservation::kUnknown;
}

inline PolicyValueObservation ClassifyValueResult(LSTATUS status) {
  if (status == ERROR_SUCCESS || status == ERROR_MORE_DATA)
    return PolicyValueObservation::kPresent;
  if (status == ERROR_FILE_NOT_FOUND) return PolicyValueObservation::kAbsent;
  return PolicyValueObservation::kUnknown;
}

inline PolicyValueObservation ObserveOnePolicy(HKEY hive, REGSAM view) {
  HKEY key = nullptr;
  const LSTATUS opened = RegOpenKeyExW(hive, kDevPolicyKey, 0,
                                     KEY_QUERY_VALUE | view, &key);
  if (opened != ERROR_SUCCESS) return ClassifyOpenResult(opened);
  // Query only existence. Even an empty or incorrectly typed value must
  // prevent treating the profile selection as policy-free. Never read data.
  DWORD bytes = 0;
  const LSTATUS queried = RegQueryValueExW(key, kProfileValue, nullptr,
                                          nullptr, nullptr, &bytes);
  RegCloseKey(key);
  return ClassifyValueResult(queried);
}

template <class Observer>
PolicyObservation ObserveProfilePolicyWith(Observer observer) {
  bool unknown = false;
  bool present = false;
  // Inspect both hives and both registry views. No early success or precedence
  // assumption: any present value is a blocker, unknown is never absence.
  const std::array<HKEY, 2> hives{HKEY_LOCAL_MACHINE, HKEY_CURRENT_USER};
  constexpr std::array<REGSAM, 2> views{KEY_WOW64_64KEY, KEY_WOW64_32KEY};
  for (auto hive : hives) {
    for (auto view : views) {
      switch (observer(hive, view)) {
        case PolicyValueObservation::kPresent: present = true; break;
        case PolicyValueObservation::kUnknown: unknown = true; break;
        case PolicyValueObservation::kAbsent: break;
        default: unknown = true; break;
      }
    }
  }
  if (present) return PolicyObservation::kOverrideObserved;
  if (unknown) return PolicyObservation::kUnknown;
  return PolicyObservation::kNoValueObserved;
}

inline PolicyObservation ObserveDevelopmentProfilePolicy() {
  return ObserveProfilePolicyWith(ObserveOnePolicy);
}
}  // namespace veil::chromium_dev
#endif
