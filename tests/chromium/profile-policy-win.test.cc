// SPDX-License-Identifier: MPL-2.0
#include "../../chromium/dev/profile-policy-win.h"
#include <iostream>
using namespace veil::chromium_dev;

int main() {
  // No writes to any registry hive. The fake observer verifies every slot.
  unsigned mask = 0;
  bool invalid = false;
  auto absent = [&](HKEY hive, REGSAM view) {
    if ((hive != HKEY_LOCAL_MACHINE && hive != HKEY_CURRENT_USER) ||
        (view != KEY_WOW64_32KEY && view != KEY_WOW64_64KEY))
      invalid = true;
    unsigned slot = (hive == HKEY_CURRENT_USER ? 2u : 0u) +
                    (view == KEY_WOW64_32KEY ? 1u : 0u);
    if ((mask & (1u << slot)) != 0) invalid = true;
    mask |= 1u << slot;
    return PolicyValueObservation::kAbsent;
  };
  if (ObserveProfilePolicyWith(absent) != PolicyObservation::kNoValueObserved ||
      invalid || mask != 15u) return 1;
  for (unsigned chosen = 0; chosen < 4; ++chosen) {
    for (auto result : {PolicyValueObservation::kPresent,
                        PolicyValueObservation::kUnknown}) {
      unsigned count = 0;
      auto simulated = [&](HKEY, REGSAM) {
        return count++ == chosen ? result : PolicyValueObservation::kAbsent;
      };
      auto expected = result == PolicyValueObservation::kPresent
          ? PolicyObservation::kOverrideObserved : PolicyObservation::kUnknown;
      if (ObserveProfilePolicyWith(simulated) != expected || count != 4) return 2;
    }
  }
  auto mixed = [](HKEY hive, REGSAM) {
    return hive == HKEY_LOCAL_MACHINE ? PolicyValueObservation::kUnknown
                                     : PolicyValueObservation::kPresent;
  };
  if (ObserveProfilePolicyWith(mixed) != PolicyObservation::kOverrideObserved)
    return 3;
  if (ClassifyOpenResult(ERROR_ACCESS_DENIED) != PolicyValueObservation::kUnknown ||
      ClassifyOpenResult(ERROR_PATH_NOT_FOUND) != PolicyValueObservation::kAbsent ||
      ClassifyOpenResult(ERROR_FILE_NOT_FOUND) != PolicyValueObservation::kAbsent ||
      ClassifyValueResult(ERROR_ACCESS_DENIED) != PolicyValueObservation::kUnknown ||
      ClassifyValueResult(ERROR_MORE_DATA) != PolicyValueObservation::kPresent ||
      ClassifyValueResult(ERROR_SUCCESS) != PolicyValueObservation::kPresent ||
      ClassifyValueResult(ERROR_FILE_NOT_FOUND) != PolicyValueObservation::kAbsent)
    return 4;
  // Exercise the read-only native path without assuming the runner's state.
  auto observed = ObserveDevelopmentProfilePolicy();
  if (observed != PolicyObservation::kUnknown &&
      observed != PolicyObservation::kOverrideObserved &&
      observed != PolicyObservation::kNoValueObserved) return 5;
  std::cout << "PASS: registry observation classifications and four slots; no policy writes or launch approval\n";
}
