// SPDX-License-Identifier: MPL-2.0
#include "include/cef_app.h"
#include "include/cef_browser.h"
#include "include/cef_client.h"
#include "include/cef_parser.h"
#include "include/cef_sandbox_win.h"
#include "include/cef_urlrequest.h"

#include <coreclr_delegates.h>
#include <hostfxr.h>
#include <nethost.h>
#include <ole2.h>

#include <algorithm>
#include <iterator>
#include <atomic>
#include <cstring>
#include <filesystem>

namespace {
CefRefPtr<CefBrowser> browser;
std::wstring title;
std::wstring page_source = L"about:blank";
bool page_fullscreen = false;
using PageStateChanged = void (*)(int);
PageStateChanged page_state_changed = nullptr;
using RenderProcessFailed = void (*)(int);
RenderProcessFailed render_process_failed = nullptr;
std::wstring profile_root;
using BeforeBrowse = int (*)(const wchar_t*, uint64_t, int);
using NavigationComplete = void (*)(uint64_t, int, int, int);
BeforeBrowse before_browse = nullptr;
NavigationComplete navigation_complete = nullptr;
unsigned popups_blocked = 0;
unsigned downloads_blocked = 0;
unsigned blob_downloads_blocked = 0;
unsigned permissions_denied = 0;
unsigned media_permissions_denied = 0;
bool fixture_enabled = false;
std::atomic<int> response_matches{0};
std::atomic<int> attachment_filtered_bytes{-1};
constexpr size_t kFixtureBodySize = 96 * 1024;
constexpr size_t kOwnedBodySize = 768 * 1024;
constexpr size_t kOwnedChunkSize = 16 * 1024;
using OwnedChunk = int (*)(const void*, int);
OwnedChunk owned_chunk = nullptr;
size_t owned_bytes = 0;
int owned_result = 0;
CefRefPtr<CefURLRequest> owned_request;
CefString armed_page_attachment;
CefString armed_attachment_source;
bool page_attachment_intercepted = false;

class OwnedAttachmentClient final : public CefURLRequestClient {
 public:
  void OnRequestComplete(CefRefPtr<CefURLRequest> request) override {
    const auto response = request->GetResponse();
    if (chunk_rejected_ && request->GetRequestStatus() == UR_CANCELED) {
      owned_result = -2;
    } else if (request->GetRequestStatus() == UR_SUCCESS && response &&
        response->GetStatus() == 200 && owned_bytes == kOwnedBodySize &&
        response->GetHeaderByName("Content-Disposition").ToString().rfind("attachment;", 0) == 0) {
      owned_result = static_cast<int>(owned_bytes);
    } else {
      owned_result = -1;
    }
    owned_request = nullptr;
    owned_chunk = nullptr;
  }
  void OnUploadProgress(CefRefPtr<CefURLRequest>, int64_t, int64_t) override {}
  void OnDownloadProgress(CefRefPtr<CefURLRequest>, int64_t, int64_t) override {}
  void OnDownloadData(CefRefPtr<CefURLRequest> request, const void* data,
                      size_t length) override {
    if (!owned_chunk || length > kOwnedBodySize - owned_bytes) {
      request->Cancel();
      return;
    }
    const auto* bytes = static_cast<const unsigned char*>(data);
    for (size_t offset = 0; offset < length;) {
      const auto count = std::min(length - offset, kOwnedChunkSize);
      if (!owned_chunk(bytes + offset, static_cast<int>(count))) {
        chunk_rejected_ = true;
        request->Cancel();
        return;
      }
      offset += count;
      owned_bytes += count;
    }
  }
  bool GetAuthCredentials(bool, const CefString&, int, const CefString&,
                          const CefString&, CefRefPtr<CefAuthCallback>) override {
    return false;
  }

 private:
  bool chunk_rejected_ = false;
  IMPLEMENT_REFCOUNTING(OwnedAttachmentClient);
};

class FixtureBodyFilter final : public CefResponseFilter {
 public:
  bool InitFilter() override { return true; }
  FilterStatus Filter(void* input, size_t input_size, size_t& input_read,
                      void* output, size_t output_size, size_t& output_written) override {
    input_read = output_written = std::min(input_size, output_size);
    if (input_read > kFixtureBodySize - bytes) return RESPONSE_FILTER_ERROR;
    if (input_read) {
      const auto* values = static_cast<const unsigned char*>(input);
      for (size_t i = 0; i < input_read; ++i)
        matches &= values[i] == (bytes + i) % 251;
      std::memcpy(output, input, input_read);
      bytes += input_read;
    }
    return input_read == input_size ? RESPONSE_FILTER_DONE : RESPONSE_FILTER_NEED_MORE_DATA;
  }

  size_t bytes = 0;
  bool matches = true;

 private:
  IMPLEMENT_REFCOUNTING(FixtureBodyFilter);
};

class FixtureResponseProbe final : public CefResourceRequestHandler {
 public:
  explicit FixtureResponseProbe(bool attachment) : attachment_(attachment) {}

  CefRefPtr<CefResponseFilter> GetResourceResponseFilter(
      CefRefPtr<CefBrowser>, CefRefPtr<CefFrame>, CefRefPtr<CefRequest>,
      CefRefPtr<CefResponse> response) override {
    if (response->GetStatus() != 200) return nullptr;
    filter_ = new FixtureBodyFilter();
    return filter_;
  }

  void OnResourceLoadComplete(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame>,
                              CefRefPtr<CefRequest>, CefRefPtr<CefResponse>,
                              URLRequestStatus status, int64_t) override {
    if (attachment_) {
      attachment_filtered_bytes.store(!filter_ ? 0 : filter_->matches ?
          static_cast<int>(filter_->bytes) : -2);
    } else {
      const bool matched = status == UR_SUCCESS && filter_ && filter_->matches &&
                           filter_->bytes == kFixtureBodySize;
      response_matches.store(matched ? 1 : -1);
    }
  }

 private:
  const bool attachment_;
  CefRefPtr<FixtureBodyFilter> filter_;
  IMPLEMENT_REFCOUNTING(FixtureResponseProbe);
};

class FixtureApp final : public CefApp {
 public:
  void OnBeforeCommandLineProcessing(const CefString& type,
                                    CefRefPtr<CefCommandLine> command_line) override {
    if (type.empty()) {
      command_line->AppendSwitch("use-fake-device-for-media-stream");
      // Only the synthetic fixture bypasses Chromium's early popup blocker to
      // exercise the mandatory host denial below. No popup is allowed.
      command_line->AppendSwitch("disable-popup-blocking");
    }
  }

 private:
  IMPLEMENT_REFCOUNTING(FixtureApp);
};

class Client final : public CefClient,
                     public CefDisplayHandler,
                     public CefLifeSpanHandler,
                     public CefRequestHandler,
                     public CefLoadHandler,
                     public CefDownloadHandler,
                     public CefPermissionHandler {
 public:
  CefRefPtr<CefDisplayHandler> GetDisplayHandler() override { return this; }
  CefRefPtr<CefLifeSpanHandler> GetLifeSpanHandler() override { return this; }
  bool OnBeforePopup(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame>, int,
                     const CefString&, const CefString&,
                     CefLifeSpanHandler::WindowOpenDisposition,
                     bool, const CefPopupFeatures&, CefWindowInfo&,
                     CefRefPtr<CefClient>&, CefBrowserSettings&,
                     CefRefPtr<CefDictionaryValue>&, bool*) override {
    ++popups_blocked;
    return true;
  }
  CefRefPtr<CefRequestHandler> GetRequestHandler() override { return this; }
  CefRefPtr<CefLoadHandler> GetLoadHandler() override { return this; }
  CefRefPtr<CefDownloadHandler> GetDownloadHandler() override { return this; }
  CefRefPtr<CefPermissionHandler> GetPermissionHandler() override { return this; }

  CefRefPtr<CefResourceRequestHandler> GetResourceRequestHandler(
      CefRefPtr<CefBrowser>, CefRefPtr<CefFrame>, CefRefPtr<CefRequest> request,
      bool, bool, const CefString&, bool&) override {
    if (!fixture_enabled || request->GetMethod() != "GET") return nullptr;
    CefURLParts parts;
    if (!CefParseURL(request->GetURL(), parts) || CefString(&parts.scheme) != "http" ||
        CefString(&parts.host) != "127.0.0.1") return nullptr;
    const CefString path(&parts.path);
    if (path == "/wpf-response") return new FixtureResponseProbe(false);
    if (path == "/wpf-attachment") return new FixtureResponseProbe(true);
    return nullptr;
  }

  bool CanDownload(CefRefPtr<CefBrowser>, const CefString& url, const CefString&) override {
    ++downloads_blocked;
    if (url.ToString().rfind("blob:", 0) == 0) ++blob_downloads_blocked;
    return false;
  }
  bool OnBeforeDownload(CefRefPtr<CefBrowser>, CefRefPtr<CefDownloadItem> item,
                        const CefString&, CefRefPtr<CefBeforeDownloadCallback>) override {
    ++downloads_blocked;
    if (item->GetURL().ToString().rfind("blob:", 0) == 0) ++blob_downloads_blocked;
    // Claim the request and let CEF discard the uncontinued callback.
    return true;
  }
  void OnDownloadUpdated(CefRefPtr<CefBrowser>, CefRefPtr<CefDownloadItem> item,
                         CefRefPtr<CefDownloadItemCallback> callback) override {
    if (item->IsInProgress()) callback->Cancel();
  }
  bool OnShowPermissionPrompt(CefRefPtr<CefBrowser>, uint64_t, const CefString&,
                              uint32_t, CefRefPtr<CefPermissionPromptCallback> callback) override {
    ++permissions_denied;
    callback->Continue(CEF_PERMISSION_RESULT_DENY);
    return true;
  }
  bool OnRequestMediaAccessPermission(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame>,
                                      const CefString&, uint32_t,
                                      CefRefPtr<CefMediaAccessCallback> callback) override {
    ++media_permissions_denied;
    callback->Cancel();
    return true;
  }
  bool OnBeforeBrowse(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame> frame,
                      CefRefPtr<CefRequest> request, bool, bool redirect) override {
    const CefString url = request->GetURL();
    if (url == "about:blank") return false;
    CefURLParts parts;
    const bool parsed = CefParseURL(url, parts);
    const CefString host(&parts.host);
    if (!parsed || CefString(&parts.scheme) != "http" ||
        (host != "127.0.0.1" && host != "[::1]" && host != "localhost")) {
      if (frame->IsMain() && redirect && active_id_) Complete(false, true, ERR_ABORTED);
      return true;
    }
    if (!frame->IsMain()) return false;
    if (fixture_enabled && !armed_page_attachment.empty() && url == armed_page_attachment) {
      if (!redirect && request->GetMethod() == "GET" && frame->GetURL() == armed_attachment_source)
        page_attachment_intercepted = true;
      return true;
    }
    // The fixture serializes top-level loads. CEF load callbacks do not carry
    // a WebView2-style navigation ID, so refuse overlapping fixture requests.
    if (active_id_ != 0 && !redirect) return true;
    const bool continuation = active_id_ != 0;
    if (!continuation) active_id_ = ++sequence_;
    active_url_ = url;
    if (!before_browse || before_browse(url.ToWString().c_str(), active_id_, continuation)) {
      Complete(0, true, ERR_ABORTED);
      return true;
    }
    return false;
  }
  void OnLoadEnd(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame> frame, int http_status) override {
    if (frame->IsMain() && frame->GetURL() == active_url_)
      Complete(http_status >= 200 && http_status < 400, false, http_status);
  }
  void OnLoadError(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame> frame,
                   ErrorCode error, const CefString&, const CefString& failed_url) override {
    if (frame->IsMain() && failed_url == active_url_)
      Complete(false, error == ERR_ABORTED, error);
  }
  void OnAddressChange(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame> frame,
                       const CefString& value) override {
    if (!frame->IsMain()) return;
    page_source = value.ToWString();
    if (page_state_changed) page_state_changed(1);
  }
  void OnTitleChange(CefRefPtr<CefBrowser>, const CefString& value) override {
    title = value.ToWString();
    if (page_state_changed) page_state_changed(2);
  }
  void OnLoadingStateChange(CefRefPtr<CefBrowser>, bool, bool, bool) override {
    if (page_state_changed) page_state_changed(3);
  }
  void OnFullscreenModeChange(CefRefPtr<CefBrowser>, bool fullscreen) override {
    page_fullscreen = fullscreen;
    if (page_state_changed) page_state_changed(4);
  }
  void OnRenderProcessTerminated(CefRefPtr<CefBrowser>, TerminationStatus,
                                 int error_code, const CefString&) override {
    // A renderer is a child process. The browser/managed host is still alive.
    // Release any serialized navigation so a user-directed retry can proceed.
    Complete(false, false, ERR_FAILED);
    if (render_process_failed) render_process_failed(error_code);
  }
  // Normal shutdown is not a crash notification. The host process cannot
  // report its own abrupt termination through this in-process callback.
  void OnBeforeClose(CefRefPtr<CefBrowser>) override {
    page_fullscreen = false;
    browser = nullptr;
  }

 private:
  void Complete(bool success, bool canceled, int code) {
    const uint64_t id = active_id_;
    active_id_ = 0;
    active_url_.clear();
    if (id && navigation_complete) navigation_complete(id, success, canceled, code);
  }

  uint64_t sequence_ = 0;
  uint64_t active_id_ = 0;
  CefString active_url_;
  IMPLEMENT_REFCOUNTING(Client);
};

int RunManagedWindow() {
  wchar_t host_path[MAX_PATH];
  size_t host_length = std::size(host_path);
  if (get_hostfxr_path(host_path, &host_length, nullptr) != 0) return 20;
  const auto host = LoadLibraryW(host_path);
  if (!host) return 21;
  // Hostfxr remains loaded for the lifetime of the managed runtime.
  const auto init = reinterpret_cast<hostfxr_initialize_for_runtime_config_fn>(
      GetProcAddress(host, "hostfxr_initialize_for_runtime_config"));
  const auto get_delegate = reinterpret_cast<hostfxr_get_runtime_delegate_fn>(
      GetProcAddress(host, "hostfxr_get_runtime_delegate"));
  const auto close = reinterpret_cast<hostfxr_close_fn>(GetProcAddress(host, "hostfxr_close"));
  if (!init || !get_delegate || !close) return 22;

  wchar_t exe[MAX_PATH];
  const DWORD length = GetModuleFileNameW(nullptr, exe, MAX_PATH);
  if (length == 0 || length == MAX_PATH) return 23;
  const auto directory = std::filesystem::path(exe).parent_path();
  const auto config = directory / L"Veil.Cef.WpfSmoke.runtimeconfig.json";
  const auto assembly = directory / L"Veil.Cef.WpfSmoke.dll";
  hostfxr_handle context = nullptr;
  const int initialized = init(config.c_str(), nullptr, &context);
  if (initialized < 0 || !context) return 24;
  void* delegate = nullptr;
  const int status = get_delegate(context, hdt_load_assembly_and_get_function_pointer, &delegate);
  close(context);
  if (status != 0 || !delegate) return 25;
  const auto load = reinterpret_cast<load_assembly_and_get_function_pointer_fn>(delegate);
  void* entry = nullptr;
  if (load(assembly.c_str(), L"Veil.Cef.WpfSmoke, Veil.Cef.WpfSmoke", L"Run",
           UNMANAGEDCALLERSONLY_METHOD, nullptr, &entry) != 0 || !entry) return 26;
  return reinterpret_cast<int (*)()>(entry)();
}
}  // namespace

extern "C" __declspec(dllexport) void VeilCefSetNavigationCallbacks(
    BeforeBrowse starting, NavigationComplete completed) {
  before_browse = starting;
  navigation_complete = completed;
}

extern "C" __declspec(dllexport) unsigned VeilCefDownloadsBlocked() { return downloads_blocked; }
extern "C" __declspec(dllexport) unsigned VeilCefBlobDownloadsBlocked() { return blob_downloads_blocked; }
extern "C" __declspec(dllexport) unsigned VeilCefPermissionsDenied() { return permissions_denied; }
extern "C" __declspec(dllexport) unsigned VeilCefMediaPermissionsDenied() { return media_permissions_denied; }
extern "C" __declspec(dllexport) int VeilCefResponseMatches() { return response_matches.load(); }
extern "C" __declspec(dllexport) int VeilCefAttachmentFilteredBytes() { return attachment_filtered_bytes.load(); }
extern "C" __declspec(dllexport) int VeilCefClickFixtureAttachment(const wchar_t* address) {
  if (!fixture_enabled || !browser || !armed_page_attachment.empty()) return 0;
  const auto frame = browser->GetMainFrame();
  const auto source = frame->GetURL();
  CefURLParts parts;
  if (!CefParseURL(source, parts) || CefString(&parts.scheme) != "http" ||
      CefString(&parts.host) != "127.0.0.1" || CefString(&parts.path) != "/wpf-policy")
    return 0;
  CefURLParts target;
  if (!CefParseURL(address, target) ||
      CefString(&target.origin) != CefString(&parts.origin) ||
      CefString(&target.path) != "/wpf-owned-attachment") return 0;
  armed_page_attachment = address;
  armed_attachment_source = source;
  page_attachment_intercepted = false;
  frame->ExecuteJavaScript("document.getElementById('vault-download').click()", source, 0);
  return 1;
}
extern "C" __declspec(dllexport) int VeilCefPageAttachmentIntercepted() {
  return page_attachment_intercepted ? 1 : 0;
}
extern "C" __declspec(dllexport) int VeilCefStartOwnedAttachment(const wchar_t* address,
                                                                 OwnedChunk chunk) {
  if (!fixture_enabled || !browser || owned_request || !chunk) return 0;
  CefString url(address);
  CefURLParts parts;
  if (!CefParseURL(url, parts) || CefString(&parts.scheme) != "http" ||
      CefString(&parts.host) != "127.0.0.1") return 0;
  const CefString path(&parts.path);
  if (path != "/wpf-owned-attachment" && path != "/wpf-owned-truncated" &&
      path != "/wpf-owned-saturated") return 0;
  if (path == "/wpf-owned-attachment") {
    if (!page_attachment_intercepted || url != armed_page_attachment) return 0;
    page_attachment_intercepted = false;
    armed_page_attachment.clear();
    armed_attachment_source.clear();
  }
  owned_bytes = 0;
  owned_result = 0;
  owned_chunk = chunk;
  const auto request = CefRequest::Create();
  request->SetURL(url);
  request->SetMethod("GET");
  if (path == "/wpf-owned-attachment")
    request->SetHeaderByName("X-Veil-Fixture-Handoff", "1", true);
  request->SetFlags(UR_FLAG_DISABLE_CACHE | UR_FLAG_STOP_ON_REDIRECT | UR_FLAG_NO_RETRY_ON_5XX);
  owned_request = CefURLRequest::Create(request, new OwnedAttachmentClient(), nullptr);
  if (!owned_request) owned_chunk = nullptr;
  return owned_request ? 1 : 0;
}
extern "C" __declspec(dllexport) int VeilCefOwnedAttachmentResult() { return owned_result; }

extern "C" __declspec(dllexport) int VeilCefOpen(HWND parent) {
  if (browser) return 0;
  CefWindowInfo window;
  window.SetAsChild(parent, CefRect(0, 0, 1, 1));
  window.runtime_style = CEF_RUNTIME_STYLE_ALLOY;
  browser = CefBrowserHost::CreateBrowserSync(window, new Client(), "about:blank",
      CefBrowserSettings(), nullptr, nullptr);
  return browser ? 1 : 0;
}

extern "C" __declspec(dllexport) void VeilCefResize(HWND parent) {
  RECT bounds{};
  if (browser && GetClientRect(parent, &bounds))
    SetWindowPos(browser->GetHost()->GetWindowHandle(), nullptr, 0, 0,
                 bounds.right, bounds.bottom, SWP_NOZORDER | SWP_NOACTIVATE);
}

extern "C" __declspec(dllexport) void VeilCefNavigate(const wchar_t* url) {
  if (browser) browser->GetMainFrame()->LoadURL(url);
}

extern "C" __declspec(dllexport) const wchar_t* VeilCefProfile() {
  return profile_root.c_str();
}
extern "C" __declspec(dllexport) int VeilCefIsMuted() {
  return browser && browser->GetHost()->IsAudioMuted();
}
extern "C" __declspec(dllexport) void VeilCefSetMuted(int muted) {
  if (browser) browser->GetHost()->SetAudioMuted(muted != 0);
}
extern "C" __declspec(dllexport) int VeilCefCanGoBack() {
  return browser && browser->CanGoBack();
}
extern "C" __declspec(dllexport) int VeilCefCanGoForward() {
  return browser && browser->CanGoForward();
}
extern "C" __declspec(dllexport) void VeilCefGoBack() {
  if (browser && browser->CanGoBack()) browser->GoBack();
}
extern "C" __declspec(dllexport) void VeilCefGoForward() {
  if (browser && browser->CanGoForward()) browser->GoForward();
}
extern "C" __declspec(dllexport) void VeilCefReload() {
  if (browser) browser->Reload();
}
extern "C" __declspec(dllexport) void VeilCefStop() {
  if (browser) browser->StopLoad();
}
extern "C" __declspec(dllexport) double VeilCefZoomLevel() {
  return browser ? browser->GetHost()->GetZoomLevel() : 0.0;
}
extern "C" __declspec(dllexport) void VeilCefSetZoomLevel(double level) {
  if (browser) browser->GetHost()->SetZoomLevel(level);
}

extern "C" __declspec(dllexport) void VeilCefSetPageStateCallback(PageStateChanged callback) {
  page_state_changed = callback;
}
extern "C" __declspec(dllexport) void VeilCefSetRenderProcessFailedCallback(RenderProcessFailed callback) {
  render_process_failed = callback;
}
extern "C" __declspec(dllexport) unsigned VeilCefPopupsBlocked() { return popups_blocked; }
extern "C" __declspec(dllexport) int VeilCefIsPageFullScreen() { return page_fullscreen ? 1 : 0; }
extern "C" __declspec(dllexport) const wchar_t* VeilCefSource() { return page_source.c_str(); }
extern "C" __declspec(dllexport) const wchar_t* VeilCefTitle() { return title.c_str(); }
extern "C" __declspec(dllexport) int VeilCefClosed() { return browser ? 0 : 1; }
extern "C" __declspec(dllexport) int VeilCefReadyToClose() {
  return !browser || browser->GetHost()->IsReadyToBeClosed();
}
extern "C" __declspec(dllexport) void VeilCefPump() { CefDoMessageLoopWork(); }
extern "C" __declspec(dllexport) int VeilCefTryClose() {
  return !browser || browser->GetHost()->TryCloseBrowser();
}

int RunWinMain(HINSTANCE instance, LPWSTR, int, void* sandbox_info, cef_version_info_t*) {
  if (!sandbox_info) return 10;
  CefMainArgs args(instance);
  const int subprocess = CefExecuteProcess(args, nullptr, sandbox_info);
  if (subprocess >= 0) return subprocess;

  const HRESULT apartment = OleInitialize(nullptr);
  if (FAILED(apartment)) return 11;
  std::error_code error;
  const auto temp = std::filesystem::temp_directory_path(error);
  const auto profile = temp / (L"veil-cef-wpf-profile-" + std::to_wstring(GetCurrentProcessId()));
  if (error || !std::filesystem::create_directory(profile, error)) {
    OleUninitialize();
    return 12;
  }

  CefSettings settings;
  settings.command_line_args_disabled = true;
  CefString(&settings.root_cache_path) = profile.wstring();
  CefString(&settings.log_file) = (profile / L"cef.log").wstring();
  CefRefPtr<CefApp> app;
  fixture_enabled = GetEnvironmentVariableW(L"VEIL_CEF_WPF_TEST_URL", nullptr, 0) != 0;
  if (fixture_enabled)
    app = new FixtureApp();
  if (!CefInitialize(args, settings, app, sandbox_info)) {
    OleUninitialize();
    return 13;
  }
  profile_root = profile.wstring();
  const int result = RunManagedWindow();
  if (owned_request) {
    const auto pending = owned_request;
    pending->Cancel();
    owned_request = nullptr;
  }
  owned_chunk = nullptr;
  profile_root.clear();
  CefShutdown();
  OleUninitialize();
  return result;
}
