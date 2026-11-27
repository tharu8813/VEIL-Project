// SPDX-License-Identifier: MPL-2.0
#pragma once
#include "protected_paths.h"

namespace veil_cef {
// Snapshot policy only: a path in an allowed directory is not evidence of
// the bytes mapped into memory, a trusted signature or future load behavior.
inline bool AllowedModulePath(const fs::path& path, const fs::path& application,
                              const fs::path& system) {
  if (!DriveAbsolute(path) || !DriveAbsolute(application) || !DriveAbsolute(system))
    return false;
  return Same(path.parent_path().native(), application.native()) ||
      Same(path.parent_path().native(), system.native());
}

// Verifies the live allocation is MEM_IMAGE, its module path and kernel-reported
// mapped filename agree with the opened disk file. This is a name/region
// observation, NOT a file-object identity or loaded-byte measurement.
bool ObserveImageBacking(HMODULE module, const fs::path& expected, bool require_single_link);
bool RestrictAndObserveLoadedModules();
}  // namespace veil_cef
