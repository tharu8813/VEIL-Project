// SPDX-License-Identifier: MPL-2.0
#include "protected_fixed_fixture.h"
#include "protected_fixture_observation.h"
#include "protected_initialization_plan.h"
#include "include/cef_app.h"
#include "include/cef_browser.h"
#include "include/cef_client.h"
#include "include/cef_frame.h"

namespace veil_cef {
namespace {
constexpr ULONGLONG kLoadBudgetMs = 15000, kCloseBudgetMs = 5000, kSampleMs = 1000;

class FixedFixtureClient final : public CefClient,
                                 public CefLifeSpanHandler,
                                 public CefDisplayHandler,
                                 public CefLoadHandler {
 public:
  CefRefPtr<CefLifeSpanHandler> GetLifeSpanHandler() override { return this; }
  CefRefPtr<CefDisplayHandler> GetDisplayHandler() override { return this; }
  CefRefPtr<CefLoadHandler> GetLoadHandler() override { return this; }
  void OnAfterCreated(CefRefPtr<CefBrowser> browser) override {
    observation.Created(browser->GetIdentifier());
  }
  void OnBeforeClose(CefRefPtr<CefBrowser> browser) override {
    observation.Closed(browser->GetIdentifier());
  }
  void OnLoadEnd(CefRefPtr<CefBrowser> browser, CefRefPtr<CefFrame> frame,
                 int) override {
    if (frame && frame->IsMain())
      observation.Load(browser->GetIdentifier(), frame->GetURL() == kProtectedSyntheticPage);
  }
  void OnTitleChange(CefRefPtr<CefBrowser> browser, const CefString& title) override {
    const auto frame = browser->GetMainFrame();
    observation.Title(browser->GetIdentifier(),
        frame && frame->GetURL() == kProtectedSyntheticPage,
        title == "VEIL_CEF_PROTECTED_TEST");
  }
  ProtectedFixtureObservation observation;
 private:
  IMPLEMENT_REFCOUNTING(FixedFixtureClient);
};
}  // namespace

ProtectedFixtureResult RunProtectedFixedFixture(const char* fixed_page,
    const std::function<bool()>& require_current) {
  if (!fixed_page || std::string_view(fixed_page) != kProtectedSyntheticPage ||
      !require_current || !require_current()) return {false, true};
  const HWND parent = CreateWindowExW(0, L"STATIC", L"VEIL CEF protected fixture",
      WS_OVERLAPPED, 0, 0, 320, 240, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
  if (!parent) return {false, true};
  // Never pass a caller-selected page, external navigation or shared client.
  CefWindowInfo window;
  window.SetAsChild(parent, CefRect(0, 0, 320, 240));
  window.runtime_style = CEF_RUNTIME_STYLE_ALLOY;
  CefRefPtr<FixedFixtureClient> client = new FixedFixtureClient();
  if (!require_current()) { DestroyWindow(parent); return {false, true}; }
  auto browser = CefBrowserHost::CreateBrowserSync(window, client, kProtectedSyntheticPage,
      CefBrowserSettings(), nullptr, nullptr);
  // An attempted creation without exact lifetime evidence is not proof
  // that no browser exists. Leave host destruction to terminal broker drain.
  if (!browser) return {false, false};
  const int id = browser->GetIdentifier();
  auto& observation = client->observation;
  if (!observation.Matches(id)) return {false, false};
  const auto started = GetTickCount64();
  auto last_sample = started;
  while (!observation.Ready() && !observation.Failed() &&
         !observation.Result().browsers_closed &&
         GetTickCount64() - started < kLoadBudgetMs) {
    if (GetTickCount64() - last_sample >= kSampleMs) {
      if (!require_current()) { observation.Fail(); break; }
      last_sample = GetTickCount64();
    }
    CefDoMessageLoopWork();
    Sleep(10);
  }
  if (!observation.Ready() || !require_current()) observation.Fail();
  if (observation.Matches(id)) {
    observation.RequestClose(id);
    browser->GetHost()->CloseBrowser(true);
  }
  const auto closing = GetTickCount64();
  while (!observation.Result().browsers_closed &&
         GetTickCount64() - closing < kCloseBudgetMs) {
    CefDoMessageLoopWork();
    Sleep(10);
  }
  auto result = observation.Result();
  if (!result.browsers_closed) return {false, false};
  browser = nullptr;
  client = nullptr;
  if (!DestroyWindow(parent)) result.passed = false;
  return result;
}
}  // namespace veil_cef
