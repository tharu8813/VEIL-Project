// SPDX-License-Identifier: MPL-2.0
#include "include/cef_app.h"
#include "include/cef_sandbox_win.h"
#include "product_preflight.h"

// Product-named candidate: only the initial no-arguments browser process
// can attempt a preflight. Job membership is a necessary condition, not
// proof of the specific desktop-owned job. No CEF or subprocess startup.
int RunWinMain(HINSTANCE, LPWSTR command_line, int, void* sandbox_info,
               cef_version_info_t*) {
  BOOL in_job = FALSE;
  const bool allowed = sandbox_info != nullptr &&
      command_line != nullptr && command_line[0] == L'\0' &&
      IsProcessInJob(GetCurrentProcess(), nullptr, &in_job) && in_job;
  return static_cast<int>(veil_cef::RunProductPreflight(allowed,
      veil_cef::LoadRegisteredBootstrap, veil_cef::VerifyRegisteredBootstrap));
}
