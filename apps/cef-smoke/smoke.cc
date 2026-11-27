// SPDX-License-Identifier: MPL-2.0
#include "include/cef_app.h"
#include "protected_paths.h"
#include "include/cef_browser.h"
#include "include/cef_client.h"
#include "include/cef_devtools_message_observer.h"
#include "include/cef_parser.h"
#include "include/cef_sandbox_win.h"

#include <filesystem>
#include <string>
#include <system_error>
#include <utility>

namespace {

class SiteTargetObserver final : public CefDevToolsMessageObserver {
 public:
  SiteTargetObserver(std::string cross_site, std::string same_site)
      : cross_site_(std::move(cross_site)), same_site_(std::move(same_site)) {}

  void OnDevToolsMethodResult(CefRefPtr<CefBrowser> browser, int id, bool success,
                             const void* result, size_t size) override {
    if (id != kMessageId) return;
    if (success && result && size > 0 && size <= 65536) {
      const auto json = CefParseJSON(
          CefString(std::string(static_cast<const char*>(result), size)), JSON_PARSER_RFC);
      const auto dict = json ? json->GetDictionary() : nullptr;
      const auto targets = dict ? dict->GetList("targetInfos") : nullptr;
      unsigned cross_site_count = 0, same_site_count = 0;
      if (targets) {
        for (size_t i = 0; i < targets->GetSize(); ++i) {
          const auto target = targets->GetDictionary(i);
          if (!target || target->GetString("type") != "iframe") continue;
          const auto url = target->GetString("url");
          if (url == cross_site_) ++cross_site_count;
          if (url == same_site_) ++same_site_count;
        }
      }
      passed = cross_site_count == 1 && same_site_count == 0;
    }
    browser->GetHost()->CloseBrowser(true);
  }

  static constexpr int kMessageId = 1;
  bool passed = false;

 private:
  const std::string cross_site_;
  const std::string same_site_;
  IMPLEMENT_REFCOUNTING(SiteTargetObserver);
};

class SmokeClient final : public CefClient,
                          public CefDisplayHandler,
                          public CefLifeSpanHandler {
 public:
  explicit SmokeClient(const std::string& port) {
    if (!port.empty())
      target_observer_ = new SiteTargetObserver("http://localhost:" + port + "/site-cross",
                                               "http://127.0.0.1:" + port + "/site-same");
  }
  CefRefPtr<CefDisplayHandler> GetDisplayHandler() override { return this; }
  CefRefPtr<CefLifeSpanHandler> GetLifeSpanHandler() override { return this; }

  void OnTitleChange(CefRefPtr<CefBrowser> browser,
                     const CefString& title) override {
    if (target_observer_) {
      if (title != "VEIL_CEF_SITE_READY" || target_requested_) return;
      target_requested_ = true;
      const auto host = browser->GetHost();
      target_registration_ = host->AddDevToolsMessageObserver(target_observer_);
      // Target.getTargets can return synchronously through the observer.
      if (!target_registration_ ||
          host->ExecuteDevToolsMethod(SiteTargetObserver::kMessageId, "Target.getTargets", nullptr) <= 0)
        host->CloseBrowser(true);
    } else if (title == "VEIL_CEF_OK" && !passed_) {
      passed_ = true;
      browser->GetHost()->CloseBrowser(true);
    }
  }

  void OnBeforeClose(CefRefPtr<CefBrowser>) override {
    target_registration_ = nullptr;
    CefQuitMessageLoop();
  }
  bool passed() const { return target_observer_ ? target_observer_->passed : passed_; }

 private:
  bool passed_ = false;
  bool target_requested_ = false;
  CefRefPtr<SiteTargetObserver> target_observer_;
  CefRefPtr<CefRegistration> target_registration_;
  IMPLEMENT_REFCOUNTING(SmokeClient);
};

}  // namespace

int RunWinMain(HINSTANCE instance, LPWSTR, int, void* sandbox_info,
               cef_version_info_t*) {
  // These are development-only hosts. Never interpret product paths as
  // permission or fall back to an unprotected temporary profile.
  if (veil_cef::HasProductEnvironment()) return 28;
  if (!sandbox_info) return 10;
  CefMainArgs args(instance);
  const int subprocess = CefExecuteProcess(args, nullptr, sandbox_info);
  if (subprocess >= 0) return subprocess;

  wchar_t site_port[6]{};
  const DWORD port_length = GetEnvironmentVariableW(L"VEIL_CEF_SITE_SMOKE_PORT", site_port, 6);
  std::string port;
  if (port_length != 0) {
    if (port_length > 5) return 15;
    for (DWORD i = 0; i < port_length; ++i) {
      if (site_port[i] < L'0' || site_port[i] > L'9') return 15;
      port.push_back(static_cast<char>(site_port[i]));
    }
    const int number = std::stoi(port);
    if (number < 1 || number > 65535 || std::to_string(number) != port) return 15;
  }
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
    CefRefPtr<SmokeClient> client = new SmokeClient(port);
    const std::string url = port.empty()
        ? "data:text/html,%3Cscript%3EsetTimeout(()=%3E"
          "document.title=%27VEIL_%27%2B%27CEF_OK%27,2500)%3C/script%3E"
        : "http://127.0.0.1:" + port + "/site-parent";
    if (CefBrowserHost::CreateBrowserSync(window, client, url,
          CefBrowserSettings(), nullptr, nullptr)) {
      CefRunMessageLoop();
      result = client->passed() ? 0 : 13;
    }
    DestroyWindow(parent);
  }
  CefShutdown();
  return result;
}
