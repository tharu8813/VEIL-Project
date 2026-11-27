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

bool RestrictAndObserveLoadedModules();
}  // namespace veil_cef
