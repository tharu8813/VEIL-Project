// SPDX-License-Identifier: MPL-2.0
#include "include/cef_app.h"
#include "include/cef_browser.h"
#include "include/cef_client.h"
#include "include/cef_sandbox_win.h"

#include <filesystem>
#include <system_error>

namespace {

class SmokeClient final : public CefClient,
                          public CefDisplayHandler,
                          public CefLifeSpanHandler {
 public:
  CefRefPtr<CefDisplayHandler> GetDisplayHandler() override { return this; }
  CefRefPtr<CefLifeSpanHandler> GetLifeSpanHandler() override { return this; }

  void OnTitleChange(CefRefPtr<CefBrowser> browser,
                     const CefString& title) override {
    if (title == "VEIL_CEF_OK" && !passed_) {
      passed_ = true;
      browser->GetHost()->CloseBrowser(true);
    }
  }

  void OnBeforeClose(CefRefPtr<CefBrowser>) override { CefQuitMessageLoop(); }
  bool passed() const { return passed_; }

 private:
  bool passed_ = false;
  IMPLEMENT_REFCOUNTING(SmokeClient);
};

}  // namespace

int RunWinMain(HINSTANCE instance, LPWSTR, int, void* sandbox_info,
               cef_version_info_t*) {
  if (!sandbox_info) return 10;
  CefMainArgs args(instance);
  const int subprocess = CefExecuteProcess(args, nullptr, sandbox_info);
  if (subprocess >= 0) return subprocess;

  std::error_code error;
  const auto temp = std::filesystem::temp_directory_path(error);
  if (error) return 11;
  const auto profile = temp / (L"veil-cef-smoke-profile-" +
                               std::to_wstring(GetCurrentProcessId()));
  if (!std::filesystem::create_directory(profile, error)) return 11;

  CefSettings settings;
  settings.command_line_args_disabled = true;
  CefString(&settings.root_cache_path) = profile.wstring();
  CefString(&settings.log_file) = (profile / L"cef.log").wstring();
  if (!CefInitialize(args, settings, nullptr, sandbox_info)) return 14;

  int result = 12;
  const HWND parent = CreateWindowExW(0, L"STATIC", L"VEIL CEF smoke",
      WS_OVERLAPPED, 0, 0, 640, 480, nullptr, nullptr, instance, nullptr);
  if (parent) {
    CefWindowInfo window;
    window.SetAsChild(parent, CefRect(0, 0, 640, 480));
    window.runtime_style = CEF_RUNTIME_STYLE_ALLOY;
    CefRefPtr<SmokeClient> client = new SmokeClient();
    if (CefBrowserHost::CreateBrowserSync(window, client,
          "data:text/html,%3Cscript%3EsetTimeout(()=%3E"
          "document.title=%27VEIL_%27%2B%27CEF_OK%27,2500)%3C/script%3E",
          CefBrowserSettings(), nullptr, nullptr)) {
      CefRunMessageLoop();
      result = client->passed() ? 0 : 13;
    }
    DestroyWindow(parent);
  }
  CefShutdown();
  return result;
}
