// SPDX-License-Identifier: MPL-2.0
#include "include/cef_app.h"
#include "include/cef_sandbox_win.h"

// Separate product-named candidate. Do not invoke CEF or the broker before
// signed package trust, registration, launch containment and lifetime gates
// are integrated. This binary must not be published as a usable browser.
int RunWinMain(HINSTANCE, LPWSTR, int, void*, cef_version_info_t*) {
  return 29;
}
