// SPDX-License-Identifier: MPL-2.0
#include "include/cef_app.h"
#include "include/cef_browser.h"
#include "include/cef_client.h"
#include "include/cef_cookie.h"
#include "include/cef_devtools_message_observer.h"
#include "include/cef_values.h"
#include "include/cef_task.h"
#include "include/cef_request_context.h"
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
#include <cmath>
#include <string>
#include <cstdlib>
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
using CookieDeleteAuthorization = int (*)(uint64_t);

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

// The visitor owns its result independently of the browser. Publish only
// when the visitor is released, including the zero-cookie case. No managed
// callback or native cookie object crosses the ABI.
struct CookieDomainScan {
  std::vector<std::wstring> domains;
  bool valid = true;
  std::atomic<bool> done{false};
};
class CookieDomainVisitor final : public CefCookieVisitor {
 public:
  explicit CookieDomainVisitor(std::shared_ptr<CookieDomainScan> scan) : scan_(std::move(scan)) {}
  ~CookieDomainVisitor() override { scan_->done.store(true, std::memory_order_release); }
  bool Visit(const CefCookie& cookie, int, int total, bool& delete_cookie) override {
    delete_cookie = false;
    const auto domain = CefString(&cookie.domain).ToWString();
    if (total > 4096 || scan_->domains.size() >= 4096 || domain.size() > 255) {
      scan_->valid = false;
      return false;
    }
    scan_->domains.push_back(domain);
    return true;
  }
 private:
  const std::shared_ptr<CookieDomainScan> scan_;
  IMPLEMENT_REFCOUNTING(CookieDomainVisitor);
};

enum class CookieDeleteScope { ExactDomain, FixtureProfile };
struct CookieDeleteScan {
  CookieDeleteScan(CookieDeleteScope delete_scope, std::wstring exact_domain, CookieDeleteAuthorization callback)
      : scope(delete_scope), domain(std::move(exact_domain)), authorize(callback) {}
  const CookieDeleteScope scope;
  const std::wstring domain;
  const CookieDeleteAuthorization authorize;
  int requested = 0;
  bool denied = false;
  bool valid = true;
  std::atomic<bool> done{false};
};

// Only a single numeric observation is retained, never a raw CDP response.
struct StorageUsageScan {
  int message_id = 0;
  int status = 0;
  double bytes = 0;
};
class StorageUsageObserver final : public CefDevToolsMessageObserver {
 public:
  explicit StorageUsageObserver(std::shared_ptr<StorageUsageScan> scan) : scan_(std::move(scan)) {}
  void OnDevToolsMethodResult(CefRefPtr<CefBrowser>, int message_id, bool success,
                             const void* result, size_t result_size) override {
    if (scan_->status != 0 || scan_->message_id <= 0 || message_id != scan_->message_id) return;
    scan_->status = -1;
    if (!success || !result || result_size == 0 || result_size > 65536) return;
    const auto value = CefParseJSON(CefString(std::string(static_cast<const char*>(result), result_size)),
                                    JSON_PARSER_RFC);
    if (!value || value->GetType() != VTYPE_DICTIONARY) return;
    const auto dict = value->GetDictionary();
    if (!dict) return;
    const auto type = dict->GetType("usage");
    if (type != VTYPE_INT && type != VTYPE_DOUBLE) return;
    const double bytes = type == VTYPE_INT ? dict->GetInt("usage") : dict->GetDouble("usage");
    if (!std::isfinite(bytes) || bytes < 0) return;
    scan_->bytes = bytes;
    scan_->status = 1;
  }
  void OnDevToolsAgentDetached(CefRefPtr<CefBrowser>) override {
    if (scan_->status == 0) scan_->status = -1;
  }
 private:
  const std::shared_ptr<StorageUsageScan> scan_;
  IMPLEMENT_REFCOUNTING(StorageUsageObserver);
};

struct StorageClearRequest {
  int message_id = 0;
  int status = 0;
};
class StorageClearObserver final : public CefDevToolsMessageObserver {
 public:
  explicit StorageClearObserver(std::shared_ptr<StorageClearRequest> request)
      : request_(std::move(request)) {}
  void OnDevToolsMethodResult(CefRefPtr<CefBrowser>, int message_id, bool success,
                             const void*, size_t result_size) override {
    if (request_->status != 0 || request_->message_id <= 0 || message_id != request_->message_id) return;
    request_->status = success && result_size <= 65536 ? 1 : -1;
  }
  void OnDevToolsAgentDetached(CefRefPtr<CefBrowser>) override {
    if (request_->status == 0) request_->status = -1;
  }
 private:
  const std::shared_ptr<StorageClearRequest> request_;
  IMPLEMENT_REFCOUNTING(StorageClearObserver);
};

// Boolean-only fixture observation. Never return DOM or credential values.
struct LoginProbe {
  int message_id = 0;
  int status = 0;  // pending=0, detected=1, not detected=2, failed=-1
};
class LoginProbeObserver final : public CefDevToolsMessageObserver {
 public:
  explicit LoginProbeObserver(std::shared_ptr<LoginProbe> probe) : probe_(std::move(probe)) {}
  void OnDevToolsMethodResult(CefRefPtr<CefBrowser>, int message_id, bool success,
                             const void* result, size_t result_size) override {
    if (probe_->status != 0 || probe_->message_id <= 0 || message_id != probe_->message_id) return;
    probe_->status = -1;
    if (!success || !result || result_size == 0 || result_size > 4096) return;
    const auto value = CefParseJSON(CefString(std::string(static_cast<const char*>(result), result_size)), JSON_PARSER_RFC);
    if (!value || value->GetType() != VTYPE_DICTIONARY) return;
    const auto response = value->GetDictionary();
    if (!response || response->HasKey("exceptionDetails")) return;
    const auto remote = response->GetDictionary("result");
    if (!remote || remote->GetString("type") != "boolean" || remote->GetType("value") != VTYPE_BOOL) return;
    probe_->status = remote->GetBool("value") ? 1 : 2;
  }
  void OnDevToolsAgentDetached(CefRefPtr<CefBrowser>) override {
    probe_->status = -1;
  }
 private:
  const std::shared_ptr<LoginProbe> probe_;
  IMPLEMENT_REFCOUNTING(LoginProbeObserver);
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
    cookie_delete.reset();
    storage_usage_registration = nullptr;
    storage_usage.reset();
    storage_clear_registration = nullptr;
    storage_clear.reset();
    login_probe_registration = nullptr;
    login_probe.reset();
  }
  void InvalidateLoginProbe() {
    if (login_probe) login_probe->status = -1;
  }

  const uint64_t id;
  const HWND parent;
  std::wstring title;
  std::wstring page_source = L"about:blank";
  bool page_fullscreen = false;
  bool closing = false;
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
  std::shared_ptr<CookieDomainScan> cookie_scan;
  std::shared_ptr<CookieDeleteScan> cookie_delete;
  std::shared_ptr<StorageUsageScan> storage_usage;
  CefRefPtr<CefRegistration> storage_usage_registration;
  std::shared_ptr<StorageClearRequest> storage_clear;
  CefRefPtr<CefRegistration> storage_clear_registration;
  std::shared_ptr<LoginProbe> login_probe;
  CefRefPtr<CefRegistration> login_probe_registration;
};

// Registry is browser-UI-thread confined. Keep one view until fixture-wide
// download state and managed multi-window shutdown are separated.
struct ViewEntry {
  std::shared_ptr<ViewState> state;
  CefRefPtr<CefBrowser> browser;
};
std::unordered_map<uint64_t, ViewEntry> views;
bool fixture_enabled = false;
bool tabbed_development_enabled = false;
constexpr unsigned kDevelopmentViewLimit = 1;
constexpr unsigned kFixtureViewLimit = 2;
unsigned ViewLimit() { return (fixture_enabled || tabbed_development_enabled) ? kFixtureViewLimit : kDevelopmentViewLimit; }
uint64_t last_view_id = 0;

std::shared_ptr<ViewState> LookupView(uint64_t id) {
  const auto entry = views.find(id);
  return entry == views.end() ? nullptr : entry->second.state;
}
CefRefPtr<CefBrowser> LookupBrowser(uint64_t id) {
  const auto entry = views.find(id);
  return entry == views.end() ? nullptr : entry->second.browser;
}
// CEF cookie visitors execute on the browser UI thread. Refuse any other
// thread rather than accessing the registry or managed dispatcher callback.
class ExactCookieDeleteVisitor final : public CefCookieVisitor {
 public:
  ExactCookieDeleteVisitor(uint64_t id, std::shared_ptr<CookieDeleteScan> scan)
      : id_(id), scan_(std::move(scan)) {}
  ~ExactCookieDeleteVisitor() override { scan_->done.store(true, std::memory_order_release); }
  bool Visit(const CefCookie& cookie, int, int total, bool& delete_cookie) override {
    delete_cookie = false;
    if (!CefCurrentlyOn(TID_UI) || total > 4096) {
      scan_->valid = false;
      return false;
    }
    const auto state = LookupView(id_);
    if (!state || state->closing || state->cookie_delete != scan_) {
      scan_->denied = true;
      return false;
    }
    if (scan_->scope == CookieDeleteScope::ExactDomain &&
        CefString(&cookie.domain).ToWString() != scan_->domain) return true;
    // Check the host immediately before each mutation, including after a
    // callback that might re-enter the registry and close the view.
    if (!scan_->authorize(id_) || state->closing || LookupView(id_) != state ||
        state->cookie_delete != scan_) {
      scan_->denied = true;
      return false;
    }
    delete_cookie = true;
    ++scan_->requested;
    return true;
  }
 private:
  const uint64_t id_;
  const std::shared_ptr<CookieDeleteScan> scan_;
  IMPLEMENT_REFCOUNTING(ExactCookieDeleteVisitor);
};

void RemoveView(const std::shared_ptr<ViewState>& state) {
  state->DetachCallbacks();
  const auto entry = views.find(state->id);
  if (entry != views.end() && entry->second.state == state)
    views.erase(entry);
  if (state->owned_transfer) state->owned_transfer->Cancel();
}
std::wstring profile_root;
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
    if (state_->closing) return true;
    const CefString url = request->GetURL();
    if (frame->IsMain()) state_->InvalidateLoginProbe();
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
    state_->InvalidateLoginProbe();
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
    state_->InvalidateLoginProbe();
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
    state->closing = true;
    state->DetachCallbacks();
    if (state->owned_transfer) state->owned_transfer->Cancel();
    const auto browser = LookupBrowser(id);
    // HwndHost is disposing: finish this browser even if normal close was
    // never requested. Keep the registry entry until OnBeforeClose.
    if (browser && !browser->GetHost()->IsReadyToBeClosed())
      browser->GetHost()->CloseBrowser(true);
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
  if (!fixture_enabled || !browser || !state || state->closing || !state->armed_page_attachment.empty()) return 0;
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
  if (!fixture_enabled || !browser || !state || state->closing || !chunk ||
      (state->owned_transfer && state->owned_transfer->active)) return 0;
  CefString url(address);
  CefURLParts parts;
  if (!CefParseURL(url, parts) || CefString(&parts.scheme) != "http" ||
      CefString(&parts.host) != "127.0.0.1") return 0;
  const CefString path(&parts.path);
  const bool parallel = path == "/wpf-owned-parallel-keep" || path == "/wpf-owned-parallel-cancel";
  if (path != "/wpf-owned-attachment" && path != "/wpf-owned-truncated" &&
      path != "/wpf-owned-saturated" && !parallel) return 0;
  if (parallel) {
    CefURLParts source;
    if (!CefParseURL(browser->GetMainFrame()->GetURL(), source) ||
        CefString(&source.origin) != CefString(&parts.origin)) return 0;
    const CefString source_path(&source.path);
    if ((path == "/wpf-owned-parallel-keep" && source_path != "/wpf-first") ||
        (path == "/wpf-owned-parallel-cancel" && source_path != "/wpf-isolation-reopened")) return 0;
  }
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
  if (views.size() >= ViewLimit() || !parent || !starting || !completed || !changed || !failed ||
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
// Read-only, synthetic fixture only. The browser's own request context
// supplies the cookie manager; no global manager or arbitrary profile path.
extern "C" __declspec(dllexport) int VeilCefStartCookieDomainScan(uint64_t id) {
  const auto state = LookupView(id);
  const auto browser = LookupBrowser(id);
  if (!fixture_enabled || !state || !browser || state->closing || state->cookie_scan) return 0;
  const auto context = browser->GetHost()->GetRequestContext();
  const auto manager = context ? context->GetCookieManager(nullptr) : nullptr;
  if (!manager) return 0;
  const auto scan = std::make_shared<CookieDomainScan>();
  state->cookie_scan = scan;
  if (!manager->VisitAllCookies(new CookieDomainVisitor(scan))) {
    state->cookie_scan.reset();
    return 0;
  }
  return 1;
}
extern "C" __declspec(dllexport) int VeilCefCookieDomainScanStatus(uint64_t id) {
  const auto state = LookupView(id);
  if (!state || state->closing || !state->cookie_scan) return -1;
  const auto& scan = state->cookie_scan;
  if (!scan->done.load(std::memory_order_acquire)) return 0;
  return scan->valid ? 1 : -1;
}
extern "C" __declspec(dllexport) int VeilCefCookieDomainCount(uint64_t id) {
  if (VeilCefCookieDomainScanStatus(id) != 1) return -1;
  return static_cast<int>(LookupView(id)->cookie_scan->domains.size());
}
extern "C" __declspec(dllexport) const wchar_t* VeilCefCookieDomainAt(uint64_t id, unsigned index) {
  if (VeilCefCookieDomainScanStatus(id) != 1) return nullptr;
  const auto& domains = LookupView(id)->cookie_scan->domains;
  return index < domains.size() ? domains[index].c_str() : nullptr;
}
extern "C" __declspec(dllexport) void VeilCefEndCookieDomainScan(uint64_t id) {
  const auto state = LookupView(id);
  if (state) state->cookie_scan.reset();
}

// Both explicit scopes share the same visitor and per-cookie authorization.
static int StartCookieDelete(uint64_t id, CookieDeleteScope scope, const std::wstring& exact,
                             CookieDeleteAuthorization authorize) {
  if (!CefCurrentlyOn(TID_UI)) return 0;
  const auto state = LookupView(id);
  const auto browser = LookupBrowser(id);
  if (!fixture_enabled || !state || !browser || state->closing || state->cookie_delete ||
      !authorize) return 0;
  if (scope == CookieDeleteScope::ExactDomain) {
    if (exact != L"127.0.0.1" && exact != L"localhost") return 0;
  } else if (scope != CookieDeleteScope::FixtureProfile || !tabbed_development_enabled || !exact.empty()) {
    return 0;
  }
  const auto context = browser->GetHost()->GetRequestContext();
  const auto manager = context ? context->GetCookieManager(nullptr) : nullptr;
  if (!manager) return 0;
  const auto scan = std::make_shared<CookieDeleteScan>(scope, exact, authorize);
  state->cookie_delete = scan;
  if (!manager->VisitAllCookies(new ExactCookieDeleteVisitor(id, scan))) {
    state->cookie_delete.reset();
    return 0;
  }
  return 1;
}
// Separate entry points: an empty or wildcard domain never means all cookies.
extern "C" __declspec(dllexport) int VeilCefStartCookieDelete(
    uint64_t id, const wchar_t* domain, CookieDeleteAuthorization authorize) {
  if (!domain) return 0;
  return StartCookieDelete(id, CookieDeleteScope::ExactDomain, domain, authorize);
}
extern "C" __declspec(dllexport) int VeilCefStartProfileCookieDelete(
    uint64_t id, CookieDeleteAuthorization authorize) {
  return StartCookieDelete(id, CookieDeleteScope::FixtureProfile, L"", authorize);
}
extern "C" __declspec(dllexport) int VeilCefCookieDeleteStatus(uint64_t id) {
  const auto state = LookupView(id);
  if (!state || state->closing || !state->cookie_delete) return -1;
  const auto& scan = state->cookie_delete;
  if (!scan->done.load(std::memory_order_acquire)) return 0;
  if (!scan->valid) return -1;
  // A partially issued deletion is not equivalent to an untouched scope.
  if (scan->denied) return scan->requested == 0 ? -2 : -1;
  return 1;
}
extern "C" __declspec(dllexport) int VeilCefCookieDeleteCount(uint64_t id) {
  if (VeilCefCookieDeleteStatus(id) != 1) return -1;
  return LookupView(id)->cookie_delete->requested;
}
extern "C" __declspec(dllexport) void VeilCefEndCookieDelete(uint64_t id) {
  const auto state = LookupView(id);
  if (state) state->cookie_delete.reset();
}

// Derive the only permitted origins from the synthetic runner's HTTP port.
// No arbitrary hosts, ports, paths, commands or CDP parameters cross the ABI.
static bool IsFixtureStorageOrigin(const std::wstring& origin) {
  if (!fixture_enabled || !tabbed_development_enabled) return false;
  wchar_t fixture_url[2048]{};
  const DWORD length = GetEnvironmentVariableW(L"VEIL_CEF_WPF_TEST_URL", fixture_url, 2048);
  if (length == 0 || length >= 2048) return false;
  CefURLParts parts;
  if (!CefParseURL(CefString(fixture_url), parts) ||
      CefString(&parts.scheme) != "http" || CefString(&parts.host) != "127.0.0.1") return false;
  const auto port = CefString(&parts.port).ToWString();
  if (port.empty() || port.find_first_not_of(L"0123456789") != std::wstring::npos) return false;
  return origin == L"http://127.0.0.1:" + port || origin == L"http://localhost:" + port;
}
// Only predeclared synthetic routes; no caller-supplied JavaScript.
bool IsFixtureLoginPage(const std::wstring& origin, const std::wstring& page) {
  if (!IsFixtureStorageOrigin(origin) || origin.rfind(L"http://127.0.0.1:", 0) != 0) return false;
  for (const auto* path : {L"/tabs-login-valid", L"/tabs-login-hidden", L"/tabs-login-ambiguous",
                          L"/tabs-login-cross-origin", L"/tabs-login-iframe", L"/tabs-login-new"})
    if (page == origin + path) return true;
  return false;
}
extern "C" __declspec(dllexport) int VeilCefStartLoginProbe(
    uint64_t id, const wchar_t* origin, const wchar_t* page) {
  if (!CefCurrentlyOn(TID_UI) || !origin || !page || !IsFixtureLoginPage(origin, page)) return 0;
  const auto state = LookupView(id);
  const auto browser = LookupBrowser(id);
  if (!state || !browser || state->closing || state->login_probe || browser->IsLoading() ||
      state->page_source != page || browser->GetMainFrame()->GetURL().ToWString() != page) return 0;
  const auto host = browser->GetHost();
  const auto expected = CefValue::Create();
  expected->SetString(CefString(page));
  const auto quoted_page = CefWriteJSON(expected, JSON_WRITER_DEFAULT);
  if (quoted_page.empty()) return 0;
  const std::string expression = "(()=>{try{if(window!==window.top||location.href!==" +
      quoted_page.ToString() + R"JS(||document.visibilityState!=='visible')return false;
      const visible=el=>{
        if(!el.isConnected||el.matches(':disabled')||el.readOnly||
           el.closest('[hidden], [inert], [aria-hidden="true"]'))return false;
        const rect=el.getBoundingClientRect();
        if(rect.width<=0||rect.height<=0)return false;
        for(let node=el;node;node=node.parentElement){
          const style=getComputedStyle(node);
          if(style.visibility!=='visible'||style.display==='none'||Number(style.opacity)===0||
             style.contentVisibility==='hidden')return false;
        }
        return true;
      };
      const token=(el,t)=>el.autocomplete.toLowerCase().split(/\s+/).includes(t);
      const passwords=[...document.querySelectorAll('input[type="password"]')];
      if(passwords.length!==1||!visible(passwords[0]))return false;
      const pass=passwords[0],form=pass.form;
      if(!form||token(pass,'new-password')||token(pass,'one-time-code')||
         new URL(form.action,location.href).origin!==location.origin||
         [...form.elements].some(el=>el.hasAttribute('formaction')&&
           new URL(el.getAttribute('formaction'),location.href).origin!==location.origin))return false;
      const users=[...form.elements].filter(el=>el instanceof HTMLInputElement&&visible(el)&&
        ['text','email','tel'].includes(el.type)&&!token(el,'one-time-code'));
      const explicit=users.filter(el=>token(el,'username'));
      return explicit.length===1||(explicit.length===0&&users.length===1);
      }catch{return false;}})())JS";
  const auto probe = std::make_shared<LoginProbe>();
  state->login_probe = probe;
  state->login_probe_registration = host->AddDevToolsMessageObserver(new LoginProbeObserver(probe));
  if (state->login_probe_registration) {
    const auto parameters = CefDictionaryValue::Create();
    parameters->SetString("expression", expression);
    parameters->SetBool("returnByValue", true);
    parameters->SetBool("silent", true);
    probe->message_id = host->ExecuteDevToolsMethod(0, "Runtime.evaluate", parameters);
    if (probe->message_id > 0) return 1;
  }
  state->login_probe_registration = nullptr;
  state->login_probe.reset();
  return 0;
}
extern "C" __declspec(dllexport) int VeilCefFixtureLoading(uint64_t id) {
  const auto browser = LookupBrowser(id);
  return !CefCurrentlyOn(TID_UI) || !browser || browser->IsLoading() ? 1 : 0;
}
extern "C" __declspec(dllexport) int VeilCefLoginProbeStatus(uint64_t id) {
  const auto state = LookupView(id);
  return !CefCurrentlyOn(TID_UI) || !state || state->closing || !state->login_probe ? -1 : state->login_probe->status;
}
extern "C" __declspec(dllexport) void VeilCefEndLoginProbe(uint64_t id) {
  const auto state = LookupView(id);
  if (CefCurrentlyOn(TID_UI) && state) {
    state->login_probe_registration = nullptr;
    state->login_probe.reset();
  }
}

extern "C" __declspec(dllexport) int VeilCefStartStorageUsage(uint64_t id, const wchar_t* origin) {
  if (!CefCurrentlyOn(TID_UI) || !origin || !IsFixtureStorageOrigin(origin)) return 0;
  const auto state = LookupView(id);
  const auto browser = LookupBrowser(id);
  if (!state || !browser || state->closing || state->storage_usage || state->storage_clear) return 0;
  const auto host = browser->GetHost();
  const auto scan = std::make_shared<StorageUsageScan>();
  state->storage_usage = scan;
  state->storage_usage_registration = host->AddDevToolsMessageObserver(new StorageUsageObserver(scan));
  if (state->storage_usage_registration) {
    const auto parameters = CefDictionaryValue::Create();
    parameters->SetString("origin", CefString(origin));
    scan->message_id = host->ExecuteDevToolsMethod(0, "Storage.getUsageAndQuota", parameters);
    if (scan->message_id > 0) return 1;
  }
  state->storage_usage_registration = nullptr;
  state->storage_usage.reset();
  return 0;
}
extern "C" __declspec(dllexport) int VeilCefStorageUsageStatus(uint64_t id) {
  const auto state = LookupView(id);
  return !state || state->closing || !state->storage_usage ? -1 : state->storage_usage->status;
}
extern "C" __declspec(dllexport) double VeilCefStorageUsageBytes(uint64_t id) {
  return VeilCefStorageUsageStatus(id) == 1 ? LookupView(id)->storage_usage->bytes : -1;
}
extern "C" __declspec(dllexport) void VeilCefEndStorageUsage(uint64_t id) {
  const auto state = LookupView(id);
  if (state) {
    state->storage_usage_registration = nullptr;
    state->storage_usage.reset();
  }
}

// The fixed allowlist matches SiteDataPolicy.OriginStorageTypes. In particular,
// cookies, all, session storage and profile-wide data are not requested.
extern "C" __declspec(dllexport) int VeilCefStartStorageClear(
    uint64_t id, const wchar_t* origin, CookieDeleteAuthorization authorize) {
  if (!CefCurrentlyOn(TID_UI) || !origin || !IsFixtureStorageOrigin(origin) || !authorize) return 0;
  const auto state = LookupView(id);
  const auto browser = LookupBrowser(id);
  if (!state || !browser || state->closing || state->storage_clear || state->storage_usage) return 0;
  const auto host = browser->GetHost();
  const auto request = std::make_shared<StorageClearRequest>();
  state->storage_clear = request;
  state->storage_clear_registration = host->AddDevToolsMessageObserver(new StorageClearObserver(request));
  if (state->storage_clear_registration) {
    const auto parameters = CefDictionaryValue::Create();
    parameters->SetString("origin", CefString(origin));
    parameters->SetString("storageTypes", "local_storage,indexeddb,cache_storage,service_workers,websql,file_systems");
    // Recheck after preparing the command. An issued CDP command cannot be
    // canceled by dropping the observer, so never report revocation as rollback.
    const bool allowed = authorize(id) != 0;
    if (allowed && LookupView(id) == state && !state->closing && state->storage_clear == request) {
      request->message_id = host->ExecuteDevToolsMethod(0, "Storage.clearDataForOrigin", parameters);
      if (request->message_id > 0) return 1;
    }
    if (state->storage_clear == request) {
      state->storage_clear_registration = nullptr;
      state->storage_clear.reset();
    }
    if (!allowed) return -2;
  } else {
    state->storage_clear.reset();
  }
  return 0;
}
// Cache-only command, sharing the per-view CDP mutation slot with origin clearing.
// The disk-backed context exists only in the fresh opt-in tab fixture.
extern "C" __declspec(dllexport) int VeilCefStartHttpCacheClear(
    uint64_t id, CookieDeleteAuthorization authorize) {
  if (!CefCurrentlyOn(TID_UI) || !fixture_enabled || !tabbed_development_enabled || !authorize) return 0;
  const auto state = LookupView(id);
  const auto browser = LookupBrowser(id);
  if (!state || !browser || state->closing || state->storage_clear || state->storage_usage ||
      state->cookie_delete) return 0;
  const auto host = browser->GetHost();
  const auto context = host->GetRequestContext();
  if (!context || profile_root.empty() ||
      context->GetCachePath().ToWString() != (std::filesystem::path(profile_root) / L"tab-fixture-cache").wstring())
    return 0;
  const auto request = std::make_shared<StorageClearRequest>();
  state->storage_clear = request;
  state->storage_clear_registration = host->AddDevToolsMessageObserver(new StorageClearObserver(request));
  if (state->storage_clear_registration) {
    const bool allowed = authorize(id) != 0;
    if (allowed && LookupView(id) == state && !state->closing && state->storage_clear == request) {
      request->message_id = host->ExecuteDevToolsMethod(0, "Network.clearBrowserCache", nullptr);
      if (request->message_id > 0) return 1;
    }
    if (state->storage_clear == request) {
      state->storage_clear_registration = nullptr;
      state->storage_clear.reset();
    }
    if (!allowed) return -2;
  } else {
    state->storage_clear.reset();
  }
  return 0;
}
extern "C" __declspec(dllexport) int VeilCefStorageClearStatus(uint64_t id) {
  const auto state = LookupView(id);
  return !state || state->closing || !state->storage_clear ? -1 : state->storage_clear->status;
}
extern "C" __declspec(dllexport) void VeilCefEndStorageClear(uint64_t id) {
  const auto state = LookupView(id);
  if (state) {
    state->storage_clear_registration = nullptr;
    state->storage_clear.reset();
  }
}

extern "C" __declspec(dllexport) int VeilCefClosed(uint64_t id) { return LookupView(id) ? 0 : 1; }
extern "C" __declspec(dllexport) unsigned VeilCefViewLimit() { return ViewLimit(); }
extern "C" __declspec(dllexport) unsigned VeilCefLiveViewCount() {
  return static_cast<unsigned>(views.size());
}
extern "C" __declspec(dllexport) int VeilCefReadyToClose(uint64_t id) {
  const auto browser = LookupBrowser(id);
  return !LookupView(id) || (browser && browser->GetHost()->IsReadyToBeClosed());
}
extern "C" __declspec(dllexport) void VeilCefPump() { CefDoMessageLoopWork(); }
extern "C" __declspec(dllexport) int VeilCefTryClose(uint64_t id) {
  const auto browser = LookupBrowser(id);
  const auto state = LookupView(id);
  if (!state) return 1;
  if (!browser) return 0;
  if (state->owned_transfer) state->owned_transfer->Cancel();
  const bool ready = browser->GetHost()->TryCloseBrowser();
  if (ready) state->closing = true;
  return ready ? 1 : 0;
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
  wchar_t tabs_mode[2]{};
  tabbed_development_enabled =
      GetEnvironmentVariableW(L"VEIL_CEF_WPF_TABS", tabs_mode, 2) == 1 && tabs_mode[0] == L'1';
  // Never enable persistent storage for the ordinary development host or the
  // download fixture. The tab runner uses a freshly created synthetic root.
  if (fixture_enabled && tabbed_development_enabled)
    CefString(&settings.cache_path) = (profile / L"tab-fixture-cache").wstring();
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
    state->closing = true;
    state->DetachCallbacks();
    if (state->owned_transfer) state->owned_transfer->Cancel();
    const auto browser = LookupBrowser(state->id);
    if (browser) browser->GetHost()->CloseBrowser(true);
  }
  remaining.clear();
  // Never call CefShutdown while a browser still has a live registry entry.
  // This is an abnormal fixture exit, not a successful shutdown.
  const ULONGLONG drain_started = GetTickCount64();
  while (!views.empty() && GetTickCount64() - drain_started < 5000) {
    CefDoMessageLoopWork();
    Sleep(1);
  }
  if (!views.empty()) {
    OutputDebugStringW(L"VEIL CEF: browser registry failed to drain before shutdown.");
    std::quick_exit(27);
  }
  profile_root.clear();
  CefShutdown();
  OleUninitialize();
  return result;
}
