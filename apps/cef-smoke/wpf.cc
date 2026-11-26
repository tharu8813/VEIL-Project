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
#include <memory>
#include <limits>
#include <utility>
#include <unordered_map>
#include <vector>

namespace {
using PageStateChanged = void (*)(int);
using RenderProcessFailed = void (*)(int);
using BeforeBrowse = int (*)(const wchar_t*, uint64_t, int);
using NavigationComplete = void (*)(uint64_t, int, int, int);
using OwnedChunk = int (*)(const void*, int);

// CEF URLRequest callbacks run on the thread that creates the request (UI here).
// A fresh transfer per request prevents late callbacks from touching a new stream.
struct OwnedTransfer {
  explicit OwnedTransfer(OwnedChunk callback) : chunk(callback) {}

  void Cancel() {
    if (!active) return;
    active = false;
    canceled = true;
    result = -1;
    chunk = nullptr;
    const auto pending = request;
    request = nullptr;
    if (pending) pending->Cancel();
  }

  OwnedChunk chunk;
  size_t bytes = 0;
  int result = 0;
  bool active = true;
  bool canceled = false;
  bool chunk_rejected = false;
  CefRefPtr<CefURLRequest> request;
};

// Owned by the CEF client; accessed on the browser UI thread only.
// Registration belongs to the same view lifetime as its metadata.
struct ViewState {
  explicit ViewState(uint64_t handle, HWND owner, BeforeBrowse starting, NavigationComplete completed,
                     PageStateChanged changed, RenderProcessFailed failed)
      : id(handle), parent(owner), before_browse(starting), navigation_complete(completed),
        page_state_changed(changed), render_process_failed(failed) {}

  void DetachCallbacks() {
    before_browse = nullptr;
    navigation_complete = nullptr;
    page_state_changed = nullptr;
    render_process_failed = nullptr;
  }

  const uint64_t id;
  const HWND parent;
  std::wstring title;
  std::wstring page_source = L"about:blank";
  bool page_fullscreen = false;
  BeforeBrowse before_browse;
  NavigationComplete navigation_complete;
  PageStateChanged page_state_changed;
  RenderProcessFailed render_process_failed;

  // UI-thread-only counters and attachment handoff. IO-thread probes retain
  // this state by shared ownership and publish only atomic observations.
  unsigned popups_blocked = 0;
  unsigned downloads_blocked = 0;
  unsigned blob_downloads_blocked = 0;
  unsigned permissions_denied = 0;
  unsigned media_permissions_denied = 0;
  std::atomic<int> response_matches{0};
  std::atomic<int> attachment_filtered_bytes{-1};
  CefString armed_page_attachment;
  CefString armed_attachment_source;
  bool page_attachment_intercepted = false;
  std::shared_ptr<OwnedTransfer> owned_transfer;
};

// Registry is browser-UI-thread confined. Keep one view until fixture-wide
// download state and managed multi-window shutdown are separated.
struct ViewEntry {
  std::shared_ptr<ViewState> state;
  CefRefPtr<CefBrowser> browser;
};
std::unordered_map<uint64_t, ViewEntry> views;
constexpr size_t kDevelopmentViewLimit = 1;
uint64_t last_view_id = 0;

std::shared_ptr<ViewState> LookupView(uint64_t id) {
  const auto entry = views.find(id);
  return entry == views.end() ? nullptr : entry->second.state;
}
CefRefPtr<CefBrowser> LookupBrowser(uint64_t id) {
  const auto entry = views.find(id);
  return entry == views.end() ? nullptr : entry->second.browser;
}
void RemoveView(const std::shared_ptr<ViewState>& state) {
  state->DetachCallbacks();
  const auto entry = views.find(state->id);
  if (entry != views.end() && entry->second.state == state)
    views.erase(entry);
  if (state->owned_transfer) state->owned_transfer->Cancel();
}
std::wstring profile_root;
bool fixture_enabled = false;
constexpr size_t kFixtureBodySize = 96 * 1024;
constexpr size_t kOwnedBodySize = 768 * 1024;
constexpr size_t kOwnedChunkSize = 16 * 1024;
class OwnedAttachmentClient final : public CefURLRequestClient {
 public:
  explicit OwnedAttachmentClient(const std::shared_ptr<OwnedTransfer>& transfer)
      : transfer_(transfer) {}
  void OnRequestComplete(CefRefPtr<CefURLRequest> request) override {
    const auto transfer = transfer_.lock();
    if (!transfer || !transfer->active) return;
    const auto response = request->GetResponse();
    if (transfer->chunk_rejected && request->GetRequestStatus() == UR_CANCELED) {
      transfer->result = -2;
    } else if (request->GetRequestStatus() == UR_SUCCESS && response &&
        response->GetStatus() == 200 && transfer->bytes == kOwnedBodySize &&
        response->GetHeaderByName("Content-Disposition").ToString().rfind("attachment;", 0) == 0) {
      transfer->result = static_cast<int>(transfer->bytes);
    } else {
      transfer->result = -1;
    }
    transfer->active = false;
    transfer->chunk = nullptr;
    transfer->request = nullptr;
  }
  void OnUploadProgress(CefRefPtr<CefURLRequest>, int64_t, int64_t) override {}
  void OnDownloadProgress(CefRefPtr<CefURLRequest>, int64_t, int64_t) override {}
  void OnDownloadData(CefRefPtr<CefURLRequest> request, const void* data,
                      size_t length) override {
    const auto transfer = transfer_.lock();
    if (!transfer || !transfer->active) return;
    if (!transfer->chunk || length > kOwnedBodySize - transfer->bytes) {
      request->Cancel();
      return;
    }
    const auto* bytes = static_cast<const unsigned char*>(data);
    for (size_t offset = 0; offset < length;) {
      if (!transfer->active || !transfer->chunk) return;
      const auto count = std::min(length - offset, kOwnedChunkSize);
      const bool accepted = transfer->chunk(bytes + offset, static_cast<int>(count)) != 0;
      if (!transfer->active) return;
      if (!accepted) {
        transfer->chunk_rejected = true;
        request->Cancel();
        return;
      }
      offset += count;
      transfer->bytes += count;
    }
  }
  bool GetAuthCredentials(bool, const CefString&, int, const CefString&,
                          const CefString&, CefRefPtr<CefAuthCallback>) override {
    return false;
  }

 private:
  const std::weak_ptr<OwnedTransfer> transfer_;
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
  FixtureResponseProbe(std::shared_ptr<ViewState> state, bool attachment)
      : state_(std::move(state)), attachment_(attachment) {}

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
      state_->attachment_filtered_bytes.store(!filter_ ? 0 : filter_->matches ?
          static_cast<int>(filter_->bytes) : -2);
    } else {
      const bool matched = status == UR_SUCCESS && filter_ && filter_->matches &&
                           filter_->bytes == kFixtureBodySize;
      state_->response_matches.store(matched ? 1 : -1);
    }
  }

 private:
  const std::shared_ptr<ViewState> state_;
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
  explicit Client(std::shared_ptr<ViewState> state) : state_(std::move(state)) {}
  CefRefPtr<CefDisplayHandler> GetDisplayHandler() override { return this; }
  CefRefPtr<CefLifeSpanHandler> GetLifeSpanHandler() override { return this; }
  bool OnBeforePopup(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame>, int,
                     const CefString&, const CefString&,
                     CefLifeSpanHandler::WindowOpenDisposition,
                     bool, const CefPopupFeatures&, CefWindowInfo&,
                     CefRefPtr<CefClient>&, CefBrowserSettings&,
                     CefRefPtr<CefDictionaryValue>&, bool*) override {
    ++state_->popups_blocked;
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
    if (path == "/wpf-response") return new FixtureResponseProbe(state_, false);
    if (path == "/wpf-attachment") return new FixtureResponseProbe(state_, true);
    return nullptr;
  }

  bool CanDownload(CefRefPtr<CefBrowser>, const CefString& url, const CefString&) override {
    ++state_->downloads_blocked;
    if (url.ToString().rfind("blob:", 0) == 0) ++state_->blob_downloads_blocked;
    return false;
  }
  bool OnBeforeDownload(CefRefPtr<CefBrowser>, CefRefPtr<CefDownloadItem> item,
                        const CefString&, CefRefPtr<CefBeforeDownloadCallback>) override {
    ++state_->downloads_blocked;
    if (item->GetURL().ToString().rfind("blob:", 0) == 0) ++state_->blob_downloads_blocked;
    // Claim the request and let CEF discard the uncontinued callback.
    return true;
  }
  void OnDownloadUpdated(CefRefPtr<CefBrowser>, CefRefPtr<CefDownloadItem> item,
                         CefRefPtr<CefDownloadItemCallback> callback) override {
    if (item->IsInProgress()) callback->Cancel();
  }
  bool OnShowPermissionPrompt(CefRefPtr<CefBrowser>, uint64_t, const CefString&,
                              uint32_t, CefRefPtr<CefPermissionPromptCallback> callback) override {
    ++state_->permissions_denied;
    callback->Continue(CEF_PERMISSION_RESULT_DENY);
    return true;
  }
  bool OnRequestMediaAccessPermission(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame>,
                                      const CefString&, uint32_t,
                                      CefRefPtr<CefMediaAccessCallback> callback) override {
    ++state_->media_permissions_denied;
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
    if (fixture_enabled && !state_->armed_page_attachment.empty() && url == state_->armed_page_attachment) {
      if (!redirect && request->GetMethod() == "GET" && frame->GetURL() == state_->armed_attachment_source)
        state_->page_attachment_intercepted = true;
      return true;
    }
    // The fixture serializes top-level loads. CEF load callbacks do not carry
    // a WebView2-style navigation ID, so refuse overlapping fixture requests.
    if (active_id_ != 0 && !redirect) return true;
    const bool continuation = active_id_ != 0;
    if (!continuation) active_id_ = ++sequence_;
    active_url_ = url;
    if (!state_->before_browse || state_->before_browse(url.ToWString().c_str(), active_id_, continuation)) {
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
    state_->page_source = value.ToWString();
    if (state_->page_state_changed) state_->page_state_changed(1);
  }
  void OnTitleChange(CefRefPtr<CefBrowser>, const CefString& value) override {
    state_->title = value.ToWString();
    if (state_->page_state_changed) state_->page_state_changed(2);
  }
  void OnLoadingStateChange(CefRefPtr<CefBrowser>, bool, bool, bool) override {
    if (state_->page_state_changed) state_->page_state_changed(3);
  }
  void OnFullscreenModeChange(CefRefPtr<CefBrowser>, bool fullscreen) override {
    state_->page_fullscreen = fullscreen;
    if (state_->page_state_changed) state_->page_state_changed(4);
  }
  void OnRenderProcessTerminated(CefRefPtr<CefBrowser>, TerminationStatus,
                                 int error_code, const CefString&) override {
    // A renderer is a child process. The browser/managed host is still alive.
    // Release any serialized navigation so a user-directed retry can proceed.
    Complete(false, false, ERR_FAILED);
    if (state_->render_process_failed) state_->render_process_failed(error_code);
  }
  // Normal shutdown is not a crash notification. The host process cannot
  // report its own abrupt termination through this in-process callback.
  void OnBeforeClose(CefRefPtr<CefBrowser>) override {
    state_->page_fullscreen = false;
    RemoveView(state_);
  }

 private:
  void Complete(bool success, bool canceled, int code) {
    const uint64_t id = active_id_;
    active_id_ = 0;
    active_url_.clear();
    if (id && state_->navigation_complete) state_->navigation_complete(id, success, canceled, code);
  }

  const std::shared_ptr<ViewState> state_;
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

extern "C" __declspec(dllexport) void VeilCefDetachCallbacks(uint64_t id, HWND parent) {
  const auto state = LookupView(id);
  if (state && state->parent == parent) {
    state->DetachCallbacks();
    if (state->owned_transfer) state->owned_transfer->Cancel();
  }
}

extern "C" __declspec(dllexport) unsigned VeilCefDownloadsBlocked(uint64_t id) {
  const auto state = LookupView(id);
  return state ? state->downloads_blocked : 0;
}
extern "C" __declspec(dllexport) unsigned VeilCefBlobDownloadsBlocked(uint64_t id) {
  const auto state = LookupView(id);
  return state ? state->blob_downloads_blocked : 0;
}
extern "C" __declspec(dllexport) unsigned VeilCefPermissionsDenied(uint64_t id) {
  const auto state = LookupView(id);
  return state ? state->permissions_denied : 0;
}
extern "C" __declspec(dllexport) unsigned VeilCefMediaPermissionsDenied(uint64_t id) {
  const auto state = LookupView(id);
  return state ? state->media_permissions_denied : 0;
}
extern "C" __declspec(dllexport) int VeilCefResponseMatches(uint64_t id) {
  const auto state = LookupView(id);
  return state ? state->response_matches.load() : 0;
}
extern "C" __declspec(dllexport) int VeilCefAttachmentFilteredBytes(uint64_t id) {
  const auto state = LookupView(id);
  return state ? state->attachment_filtered_bytes.load() : -1;
}
extern "C" __declspec(dllexport) int VeilCefClickFixtureAttachment(uint64_t id, const wchar_t* address) {
  const auto browser = LookupBrowser(id);
  const auto state = LookupView(id);
  if (!fixture_enabled || !browser || !state || !state->armed_page_attachment.empty()) return 0;
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
  state->armed_page_attachment = address;
  state->armed_attachment_source = source;
  state->page_attachment_intercepted = false;
  frame->ExecuteJavaScript("document.getElementById('vault-download').click()", source, 0);
  return 1;
}
extern "C" __declspec(dllexport) int VeilCefPageAttachmentIntercepted(uint64_t id) {
  const auto state = LookupView(id);
  return state && state->page_attachment_intercepted ? 1 : 0;
}
extern "C" __declspec(dllexport) int VeilCefStartOwnedAttachment(uint64_t id, const wchar_t* address,
                                                                 OwnedChunk chunk) {
  const auto browser = LookupBrowser(id);
  const auto state = LookupView(id);
  if (!fixture_enabled || !browser || !state || !chunk ||
      (state->owned_transfer && state->owned_transfer->active)) return 0;
  CefString url(address);
  CefURLParts parts;
  if (!CefParseURL(url, parts) || CefString(&parts.scheme) != "http" ||
      CefString(&parts.host) != "127.0.0.1") return 0;
  const CefString path(&parts.path);
  if (path != "/wpf-owned-attachment" && path != "/wpf-owned-truncated" &&
      path != "/wpf-owned-saturated") return 0;
  if (path == "/wpf-owned-attachment") {
    if (!state->page_attachment_intercepted || url != state->armed_page_attachment) return 0;
    state->page_attachment_intercepted = false;
    state->armed_page_attachment.clear();
    state->armed_attachment_source.clear();
  }
  const auto transfer = std::make_shared<OwnedTransfer>(chunk);
  state->owned_transfer = transfer;
  const auto request = CefRequest::Create();
  request->SetURL(url);
  request->SetMethod("GET");
  if (path == "/wpf-owned-attachment")
    request->SetHeaderByName("X-Veil-Fixture-Handoff", "1", true);
  request->SetFlags(UR_FLAG_DISABLE_CACHE | UR_FLAG_STOP_ON_REDIRECT | UR_FLAG_NO_RETRY_ON_5XX);
  const auto created = CefURLRequest::Create(request, new OwnedAttachmentClient(transfer), nullptr);
  if (!created) {
    transfer->Cancel();
    return 0;
  }
  if (transfer->active) {
    transfer->request = created;
  } else if (transfer->canceled) {
    created->Cancel();
    return 0;
  }
  return 1;
}
extern "C" __declspec(dllexport) int VeilCefOwnedAttachmentResult(uint64_t id) {
  const auto state = LookupView(id);
  return state && state->owned_transfer ? state->owned_transfer->result : 0;
}
extern "C" __declspec(dllexport) void VeilCefCancelOwnedAttachment(uint64_t id) {
  const auto state = LookupView(id);
  if (state && state->owned_transfer) state->owned_transfer->Cancel();
}

extern "C" __declspec(dllexport) uint64_t VeilCefOpen(
    HWND parent, BeforeBrowse starting, NavigationComplete completed,
    PageStateChanged changed, RenderProcessFailed failed) {
  if (views.size() >= kDevelopmentViewLimit || !parent || !starting || !completed || !changed || !failed ||
      last_view_id == (std::numeric_limits<uint64_t>::max)())
    return 0;
  auto state = std::make_shared<ViewState>(++last_view_id, parent, starting, completed, changed, failed);
  views.emplace(state->id, ViewEntry{state, nullptr});
  CefWindowInfo window;
  window.SetAsChild(parent, CefRect(0, 0, 1, 1));
  window.runtime_style = CEF_RUNTIME_STYLE_ALLOY;
  const auto created = CefBrowserHost::CreateBrowserSync(window, new Client(state), "about:blank",
      CefBrowserSettings(), nullptr, nullptr);
  // Synchronous creation may re-enter client callbacks; never retain an iterator
  // across that call or republish a view already removed by OnBeforeClose.
  const auto entry = views.find(state->id);
  if (!created || entry == views.end() || entry->second.state != state) {
    RemoveView(state);
    return 0;
  }
  entry->second.browser = created;
  return state->id;
}

extern "C" __declspec(dllexport) void VeilCefResize(uint64_t id, HWND parent) {
  const auto state = LookupView(id);
  const auto browser = LookupBrowser(id);
  RECT bounds{};
  if (state && state->parent == parent && browser && GetClientRect(parent, &bounds))
    SetWindowPos(browser->GetHost()->GetWindowHandle(), nullptr, 0, 0,
                 bounds.right, bounds.bottom, SWP_NOZORDER | SWP_NOACTIVATE);
}

extern "C" __declspec(dllexport) void VeilCefNavigate(uint64_t id, const wchar_t* url) {
  const auto browser = LookupBrowser(id);
  if (browser) browser->GetMainFrame()->LoadURL(url);
}

extern "C" __declspec(dllexport) const wchar_t* VeilCefProfile() {
  return profile_root.c_str();
}
extern "C" __declspec(dllexport) int VeilCefIsMuted(uint64_t id) {
  const auto browser = LookupBrowser(id);
  return browser && browser->GetHost()->IsAudioMuted();
}
extern "C" __declspec(dllexport) void VeilCefSetMuted(uint64_t id, int muted) {
  const auto browser = LookupBrowser(id);
  if (browser) browser->GetHost()->SetAudioMuted(muted != 0);
}
extern "C" __declspec(dllexport) int VeilCefCanGoBack(uint64_t id) {
  const auto browser = LookupBrowser(id);
  return browser && browser->CanGoBack();
}
extern "C" __declspec(dllexport) int VeilCefCanGoForward(uint64_t id) {
  const auto browser = LookupBrowser(id);
  return browser && browser->CanGoForward();
}
extern "C" __declspec(dllexport) void VeilCefGoBack(uint64_t id) {
  const auto browser = LookupBrowser(id);
  if (browser && browser->CanGoBack()) browser->GoBack();
}
extern "C" __declspec(dllexport) void VeilCefGoForward(uint64_t id) {
  const auto browser = LookupBrowser(id);
  if (browser && browser->CanGoForward()) browser->GoForward();
}
extern "C" __declspec(dllexport) void VeilCefReload(uint64_t id) {
  const auto browser = LookupBrowser(id);
  if (browser) browser->Reload();
}
extern "C" __declspec(dllexport) void VeilCefReloadIgnoringCache(uint64_t id) {
  const auto browser = LookupBrowser(id);
  if (browser) browser->ReloadIgnoreCache();
}
extern "C" __declspec(dllexport) void VeilCefStop(uint64_t id) {
  const auto browser = LookupBrowser(id);
  if (browser) browser->StopLoad();
}
extern "C" __declspec(dllexport) double VeilCefZoomLevel(uint64_t id) {
  const auto browser = LookupBrowser(id);
  return browser ? browser->GetHost()->GetZoomLevel() : 0.0;
}
extern "C" __declspec(dllexport) void VeilCefSetZoomLevel(uint64_t id, double level) {
  const auto browser = LookupBrowser(id);
  if (browser) browser->GetHost()->SetZoomLevel(level);
}

extern "C" __declspec(dllexport) unsigned VeilCefPopupsBlocked(uint64_t id) {
  const auto state = LookupView(id);
  return state ? state->popups_blocked : 0;
}
extern "C" __declspec(dllexport) int VeilCefIsPageFullScreen(uint64_t id) {
  const auto state = LookupView(id);
  return state && state->page_fullscreen ? 1 : 0;
}
extern "C" __declspec(dllexport) const wchar_t* VeilCefSource(uint64_t id) {
  const auto state = LookupView(id);
  return state ? state->page_source.c_str() : L"";
}
extern "C" __declspec(dllexport) const wchar_t* VeilCefTitle(uint64_t id) {
  const auto state = LookupView(id);
  return state ? state->title.c_str() : L"";
}
extern "C" __declspec(dllexport) int VeilCefClosed(uint64_t id) { return LookupBrowser(id) ? 0 : 1; }
extern "C" __declspec(dllexport) int VeilCefReadyToClose(uint64_t id) {
  const auto browser = LookupBrowser(id);
  return !browser || browser->GetHost()->IsReadyToBeClosed();
}
extern "C" __declspec(dllexport) void VeilCefPump() { CefDoMessageLoopWork(); }
extern "C" __declspec(dllexport) int VeilCefTryClose(uint64_t id) {
  const auto browser = LookupBrowser(id);
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
  // Defensive cleanup if the managed host exits unexpectedly. Cancel using
  // a snapshot because native callbacks may re-enter the view registry.
  std::vector<std::shared_ptr<ViewState>> remaining;
  for (const auto& entry : views) remaining.push_back(entry.second.state);
  for (const auto& state : remaining) {
    state->DetachCallbacks();
    if (state->owned_transfer) state->owned_transfer->Cancel();
  }
  profile_root.clear();
  CefShutdown();
  OleUninitialize();
  return result;
}
