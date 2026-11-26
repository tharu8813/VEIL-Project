// SPDX-License-Identifier: MPL-2.0
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
const browser = await readFile('apps/desktop/BrowserWindow.xaml.cs', 'utf8');
const tabs = await readFile('apps/desktop/BrowserWindow.Tabs.cs', 'utf8');
const motion = await readFile('apps/desktop/BrowserWindow.Motion.cs', 'utf8');
const webView2 = await readFile('apps/desktop/Engine/WebView2BrowserEngine.cs', 'utf8');

test('WebView2 synchronously applies shared navigation policy before loading a new tab', () => {
    const open = browser.slice(browser.indexOf('private async Task<BrowserTab?> OpenTab'),
        browser.indexOf('// ── Engine events'));
    assert.match(open, /await engineView\.InitializeAsync\(\);[\s\S]*?engineView\.NavigationStarting \+= \(_, args\) => tab\.Owner\.OnNavigationStarting\(tab, args\);[\s\S]*?ConfigureWebView2Tab\(tab\);[\s\S]*?engineView\.Navigate\(url\)/);
    assert.match(webView2, /var navigation = new EngineNavigationStartingEventArgs\(args\.Uri, args\.NavigationId, args\.IsRedirected\)\s*\{\s*Cancel = args\.Cancel,\s*\};\s*NavigationStarting\?\.Invoke\(this, navigation\);\s*args\.Cancel = navigation\.Cancel;/);
});
test('closing tab invalidates engine events and native actions before animation', () => {
    assert.match(browser, /private bool AcceptTabEvents\(BrowserTab tab\) => !closing && !tab\.Closing && !session\.IsEnding && !session\.EngineLost &&\s*ReferenceEquals\(tab\.Owner, this\)/);
    assert.match(browser, /OnNavigationStarting\(BrowserTab tab,[^\n]+\n\s*\{\s*if \(!AcceptTabEvents\(tab\)\) \{ args\.Cancel = true; return; \}/);
    assert.match(browser, /OnDownloadStarting\(BrowserTab tab,[^\n]+\n\s*\{\s*if \(!AcceptTabEvents\(tab\)\) \{ args\.Cancel = true; args\.Handled = true; return; \}/);
    const close = browser.slice(browser.indexOf('private async Task CloseTabAsync'), browser.indexOf('private void TabChanged'));
    assert.match(close, /tab\.Closing = true;\s*tab\.NavigationEpoch\+\+;[\s\S]*?ClearManagement\(tab\);[\s\S]*?await AnimateTabClose\(item\)/);
    assert.match(tabs, /item\.Tag is BrowserTab \{ Closing: false \} tab && tab\.Owner == this/);
    assert.match(motion, /finally \{ closingTabs\.Remove\(item\); \}/);
});
test('popup handoff requires the original live opener and navigation epoch', () => {
    const popup = browser.slice(browser.indexOf('private async Task OnNewWindowRequested'), browser.indexOf('private void UpdateAudio'));
    assert.match(popup, /long epoch = opener\.NavigationEpoch;[\s\S]*?await OpenTab\([\s\S]*?if \(CanUseTab\(opener\.Item\) && opener\.NavigationEpoch == epoch && CanUseTab\(popup\.Item\)\)\s*attached = args\.TryAttach\(popup\.EngineView\);/);
    assert.match(popup, /finally\s*\{\s*if \(!attached && !closing && CanUseTab\(popup\.Item\)\)\s*await CloseTabAsync\(popup\.Item\);/);
    assert.match(popup, /if \(CanUseTab\(tab\.Item\) && tab\.NavigationEpoch == epoch\) CloseTab\(tab\.Item\);/);
});


const session = await readFile('apps/desktop/BrowserSession.cs', 'utf8');
test('tab transfer keeps source alive until adoption and defers intermediate snapshots', () => {
    const transfer = tabs.slice(tabs.indexOf('private bool TransferTabTo'), tabs.indexOf('private void AttachTab'));
    assert.match(transfer, /session\.TabTransferDepth\+\+;[\s\S]*?DetachTab\(item\);[\s\S]*?target\.AttachTab\(item, index\);\s*if \(Tabs\.Items\.Count == 0\) Close\(\)/);
    assert.match(transfer, /catch \(Exception error\) when \(RecoverableFailure\.Is\(error\)\)[\s\S]*?tab\.Owner = this;[\s\S]*?Pages\.Children\.Add\(tab\.EngineView\.Element\)[\s\S]*?Tabs\.Items\.Insert\(TabInsertionIndex\(tab\.Pinned, originalIndex\), item\)/);
    assert.match(transfer, /finally\s*\{\s*session\.TabTransferDepth--;\s*SaveSession\(\);/);
    const detach = transfer.slice(transfer.indexOf('private void DetachTab'));
    assert.doesNotMatch(detach, /\b(?:Close|SaveTabs|SaveSession)\(\)/);
    assert.match(session, /if \(IsPrivate \|\| TabTransferDepth != 0\) return;/);
});
test('a new window initializes before it adopts the tab and saves the completed layout', () => {
    const move = tabs.slice(tabs.indexOf('private BrowserWindow MoveTabToNewWindow'), tabs.indexOf('private static void DragWindowThenMerge'));
    assert.doesNotMatch(move, /DetachTab\(/);
    assert.match(move, /OpenWindow\(\[\], 0, cascade: true, adopt: item/);
    assert.match(browser, /adoptionSource = \(adopt\?\.Tag as BrowserTab\)\?\.Owner;/);
    assert.match(browser, /await session\.EngineHost\.InitializeAsync[\s\S]*?adoptionSource\.TransferTabTo\(adopted, this, 0, initializingTarget: true\)/);
    assert.match(browser, /restoring = false;\s*if \(adopted is not null\) SaveSession\(\);/);
});


const search = await readFile('apps/desktop/BrowserWindow.Search.cs', 'utf8');
const searchPolicy = await readFile('apps/desktop/SearchPolicy.cs', 'utf8');
test('address bar resolves explicit searches and rejected addresses before implicit search', () => {
    const resolve = browser.slice(browser.indexOf('internal static Uri? ResolveAddress'), browser.indexOf('private string BuildInternalPage'));
    assert.match(resolve, /AddressInputPolicy\.HasUnsafeCharacters\(input\)[\s\S]*?TryExplicitSearch\(input, searchEngine, out var search\)[\s\S]*?AddressInputPolicy\.TryResolve\(input, out var address\)\) return address;\s*return SearchPolicy\.Search/);
    assert.match(searchPolicy, /AddressInputPolicy\.TryResolve\(query, out var address\) && address is null/);
});
test('local suggestions cannot survive programmatic address or tab context changes', () => {
    assert.match(search, /private void SetAddressText\(string text\)\s*\{\s*CloseAddressSuggestions\(\);/);
    assert.match(search, /suggestionEpoch = tab\.NavigationEpoch;\s*suggestionInput = Address\.Text;/);
    assert.match(search, /CanUseTab\(tab\.Item\) && tab\.NavigationEpoch == suggestionEpoch &&\s*Address\.Text == suggestionInput/);
});


const management = await readFile('apps/desktop/BrowserWindow.Management.cs', 'utf8');
const settings = await readFile('apps/desktop/BrowserWindow.Settings.cs', 'utf8');
test('native management pages reuse pending editors across live windows', () => {
    assert.match(management, /foreach \(var window in session\.Windows\.ToArray\(\)\)[\s\S]*?window\.FindManagementTab\(route\) is \{ CancelManagement: not null \} tab[\s\S]*?window\.Tabs\.SelectedItem = tab\.Item;[\s\S]*?window\.openingManagement\.Contains\(route\)/);
    assert.match(management, /management && tab\.ManagementSurface is null && !tab\.Closing && !closingTabs\.Contains\(item\)/);
});
test('management acceptance is bound to native page, navigation and focus epoch', () => {
    assert.match(management, /bool ContextValid\(\) => Active == tab && tab\.ManagementSurface == page &&\s*tab\.Url == route && tab\.NavigationEpoch == epoch && CanUseTab\(tab\.Item\) && SettingsReady\(\) && IsActive;/);
    assert.match(management, /if \(completion\.Task\.IsCompleted \|\| !ContextValid\(\)\) return;\s*confirmedFocus = managementFocusEpoch;\s*completion\.TrySetResult\(accepted\);/);
    assert.match(management, /accepted = accepted && ContextValid\(\) && confirmedFocus == managementFocusEpoch;/);
    assert.doesNotMatch(management, /await CloseTabAsync\(tab\.Item\)/);
    assert.match(browser, /Deactivated \+= \(_, _\) => managementFocusEpoch\+\+;/);
    assert.match(browser, /private void TabChanged[\s\S]*?managementFocusEpoch\+\+;/);
    assert.match(settings, /source\.NavigationEpoch != epoch \|\| managementFocusEpoch != focus/);
});


const library = await readFile('apps/desktop/LibraryWindow.cs', 'utf8');
const toolbar = await readFile('apps/desktop/BrowserWindow.Toolbar.cs', 'utf8');
test('bookmark and history edits both require the current live native manager', () => {
    assert.match(browser, /bool CanEditLibrary\(\) => SettingsReady\(\) && IsActive &&\s*Active is \{ CancelManagement: not null \} active && active\.Url == internalUrl &&\s*CanUseTab\(active\.Item\) && ReferenceEquals\(editable \? bookmarkLibrary : historyLibrary, dialog\)/);
    assert.match(browser, /data\.BookmarkFolders : null, CanEditLibrary, PublishLibrary/);
    assert.match(library, /bool Commit\(Action change\)\s*\{\s*if \(canEdit\?\.Invoke\(\) != true\) return false;/);
    assert.match(library, /void Open\(\)\s*\{\s*if \(canEdit\?\.Invoke\(\) != true/);
});
test('failed library save restores data and successful save publishes separately', () => {
    assert.match(library, /var previous = entries\.ToArray\(\);[\s\S]*?change\(\);[\s\S]*?committed = \(folders is null \|\| BookmarkPolicy\.Valid\(folders, entries\)\) && save\(\);[\s\S]*?if \(!committed\)[\s\S]*?entries\.AddRange\(previous\);[\s\S]*?folders\.AddRange\(previousFolders\);[\s\S]*?return false;[\s\S]*?published\?\.Invoke\(\);/);
    assert.match(library, /new HashSet<PageEntry>\(targets, ReferenceEqualityComparer\.Instance\);\s*Commit\(\(\) => entries\.RemoveAll\(confirmed\.Contains\)\);/);
    assert.match(toolbar, /private void RefreshHistoryLibraries[\s\S]*?window\.historyLibrary is \{ \} library && !ReferenceEquals\(library, source\)/);
});


test('recent download popup caches only the newest eight stored entries and drops them on lock or close', () => {
    assert.match(toolbar, /var entries = visible \? session\.Vault\.Entries : null;\s*if \(!ReferenceEquals\(popupEntrySource, entries\)\)/);
    assert.match(toolbar, /popupStored = entries is null \? \[\] : entries\.OrderByDescending\(entry => entry\.SavedAt\)\s*\.Take\(8\)/);
    assert.match(toolbar, /private void DownloadPopupClosed[\s\S]*?popupEntrySource = null;\s*popupStored = \[\];/);
    assert.match(toolbar, /DownloadPopupEmpty\.Visibility = visible && rows\.Length == 0/);
    assert.match(toolbar, /items\.Any\(item => item\.IsIndeterminate\)/);
});


const permissions = await readFile('apps/desktop/BrowserWindow.Permissions.cs', 'utf8');
const siteData = await readFile('apps/desktop/BrowserWindow.SiteData.cs', 'utf8');
test('permission grants require the original live tab and selection epoch', () => {
    assert.match(permissions, /args\.SavesInProfile = false;\s*args\.State = CoreWebView2PermissionState\.Deny;\s*if \(session\.IsPrivate\) return;/);
    assert.match(permissions, /!CanUseTab\(tab\.Item\) \|\| Active != tab \|\| !IsActive \|\| tab\.NavigationEpoch != epoch \|\|\s*permissionSelectionEpoch != selectionEpoch/);
    assert.match(permissions, /if \(accepted && PermissionContextMatches\(tab, origin, epoch, selectionEpoch\)/);
    assert.match(permissions, /!SitePermissionPolicy\.ValidRules\(dialog\.Result\.ToList\(\)\)/);
    assert.match(browser, /managementFocusEpoch\+\+;\s*permissionSelectionEpoch\+\+;/);
});
test('site-data commands require original live regular WebView and exclusive selected scope', () => {
    assert.match(siteData, /bool SourceStillValid\(\) => SiteDataReady\(\) && IsActive && Active == source && CanUseTab\(source\.Item\) &&\s*source\.NavigationEpoch == epoch && ReferenceEquals\(source\.View\.CoreWebView2, web\) &&\s*!web\.Profile\.IsInPrivateModeEnabled;/);
    assert.match(siteData, /SiteDataPolicy\.Summarize\(await reader\.GetCookieDomainsAsync\(\)\);\s*if \(!SourceStillValid\(\) \|\| permissionSelectionEpoch != selectionEpoch/);
    assert.match(siteData, /!SourceStillValid\(\) \|\| session\.ActiveDownloads\.Count != 0 \|\| !SiteDataPolicy\.ValidSelection\(selection\)/);
    assert.match(siteData, /bool StillAuthorized\(\) => SourceStillValid\(\) && permissionSelectionEpoch == confirmedSelectionEpoch &&\s*session\.ActiveDownloads\.Count == 0;/);
    assert.match(siteData, /await writer\.DeleteCookieDomainAsync\(domain, StillAuthorized\);\s*if \(requested is null\) return;/);
});


const passwordUi = await readFile('apps/desktop/BrowserWindow.Passwords.cs', 'utf8');
const loginForm = await readFile('apps/desktop/LoginFormPolicy.cs', 'utf8');
test('login form probe, read and fill are bound to exact original URL in the script', () => {
    assert.match(loginForm, /window !== window\.top \|\| location\.origin !==[\s\S]*?location\.href !== new URL\(\{\{JsonSerializer\.Serialize\(page\)\}\}\)\.href/);
    assert.match(loginForm, /el\.closest\('\[hidden\], \[inert\], \[aria-hidden="true"\]'\)/);
    assert.match(loginForm, /Number\(style\.opacity\) === 0 \|\| style\.contentVisibility === 'hidden'/);
    assert.match(passwordUi, /LoginFormPolicy\.Probe\(origin, page\)/);
    assert.match(passwordUi, /LoginFormPolicy\.Read\(origin, page\)/);
    assert.match(passwordUi, /bool Current\(\) => tab\.Url == page && PermissionContextMatches\(tab, login\.Origin, epoch, selectionEpoch\);\s*if \(!Current\(\)\) return;\s*string result = await tab\.View\.CoreWebView2\.ExecuteScriptAsync\(LoginFormPolicy\.Fill\(login, page\)\);\s*if \(Current\(\)\)/);
    assert.doesNotMatch(loginForm, /\.submit\(|\.requestSubmit\(/);
});


test('login fill uses double-dollar raw interpolation for JavaScript braces', () => {
    assert.match(loginForm, /Script\(login\.Origin, page, \$\$"""/);
    assert.match(loginForm, /set\.call\(pass, \{\{JsonSerializer\.Serialize\(login\.Password\)\}\}\);/);
});


test('deferred keyboard commands preserve the original page and leave composition keys alone', () => {
    assert.match(tabs, /IsCompositionKey\(Key key\) => key is Key\.ImeProcessed or Key\.DeadCharProcessed/);
    assert.match(tabs, /if \(IsCompositionKey\(key\)\) return;/);
    assert.match(tabs, /if \(!windowCommand && \(managementFocusEpoch != originalFocus \|\| Active != originalTab \|\|[\s\S]*?originalTab\.NavigationEpoch != originalEpoch\)\)\) return;/);
    assert.match(tabs, /CloseTab\(originalTab\.Item\)/);
    assert.match(browser, /private void AddressKeyDown[\s\S]*?if \(args\.Handled \|\| IsCompositionKey\(args\.Key\)\) return;[\s\S]*?if \(args\.IsRepeat\) return;\s*SubmitAddress/);
});
test('page find ignores old requests after tab, query or navigation changes', () => {
    assert.match(browser, /long request = \+\+findRequestEpoch;/);
    assert.match(browser, /bool Current\(\) => request == findRequestEpoch && IsActive && Active == tab && CanUseTab\(tab\.Item\) &&\s*tab\.NavigationEpoch == navigation && permissionSelectionEpoch == selection && FindText\.Text == query;/);
    assert.match(browser, /bool\? result = await tab\.EngineView\.FindTextAsync\(query, backwards\);\s*if \(Current\(\)\) Status\.Text = result switch\s*\{\s*true => "일치하는 텍스트를 찾았습니다\."/);
    assert.match(browser, /private void FindCloseClick[\s\S]*?findRequestEpoch\+\+;\s*ShowFindBar\(false\);/);
});


const networkUi = await readFile('apps/desktop/BrowserWindow.Network.cs', 'utf8');
const threatsUi = await readFile('apps/desktop/BrowserWindow.Threats.cs', 'utf8');
test('navigation completion and deferred protection actions are scoped to the live navigation', () => {
    assert.match(browser, /if \(!AcceptTabEvents\(tab\) \|\| tab\.NavigationId != args\.NavigationId\) return;/);
    assert.match(browser, /if \(tab\.NavigationId != args\.NavigationId\)[\s\S]*?tab\.ExpectedUpgrade = null;\s*tab\.NavigationId = args\.NavigationId;/);
    assert.match(browser, /if \(tab\.UpgradedFrom is \{ \} plain && tab\.ExpectedUpgrade is null\)/);
    assert.match(networkUi, /private void QueueNavigationAction[\s\S]*?long epoch = tab\.NavigationEpoch;[\s\S]*?if \(!CanUseTab\(tab\.Item\) \|\| tab\.NavigationEpoch != epoch\) return;/);
    assert.match(networkUi, /tab\.ExpectedUpgrade = secure;\s*QueueNavigationAction\(tab,/);
    assert.match(threatsUi, /QueueNavigationAction\(tab, \(\) => ShowThreatWarning/);
    assert.match(networkUi, /PendingHttp: \{ \} plain \} tab \|\| !CanUseTab\(tab\.Item\)/);
    assert.match(threatsUi, /PendingThreat: \{ \} blocked \} tab \|\| !CanUseTab\(tab\.Item\)/);
});


test('tab visuals, focus and native management visibility use the shared engine element', () => {
    const open = browser.slice(browser.indexOf('private async Task<BrowserTab?> OpenTab'), browser.indexOf('// ── Engine events'));
    assert.match(open, /if \(session\.EngineHost is not WebView2BrowserEngine\)\s*throw new InvalidOperationException[\s\S]*?var element = engineView\.Element;\s*[\s\S]*?Pages\.Children\.Add\(element\)/);
    assert.match(browser, /Pages\.Children\.Remove\(tab\.EngineView\.Element\);\s*tab\.EngineView\.Dispose\(\)/);
    assert.match(browser, /tab\.EngineView\.Element\.Visibility = Visibility\.Visible;\s*tab\.EngineView\.Element\.Focus\(\);\s*tab\.EngineView\.Navigate\(/);
    assert.match(tabs, /Pages\.Children\.Remove\(tab\.EngineView\.Element\)/);
    assert.match(tabs, /Pages\.Children\.Add\(tab\.EngineView\.Element\)/);
    assert.match(tabs, /EngineView\.Element\.Visibility =/);
    assert.match(management, /tab\.EngineView\.Element\.Visibility = selected && !management/);
    for (const source of [browser, tabs, management])
        assert.doesNotMatch(source, /Pages\.Children\.(?:Add|Remove|Contains)\(tab\.View\)|tab\.View\.(?:Visibility|Focus\b)/);
});


const webView2Window = await readFile('apps/desktop/BrowserWindow.WebView2.cs', 'utf8');
const cefView = await readFile('apps/cef-smoke/wpf/CefDevelopmentEngine.cs', 'utf8');
test('page metadata and history availability route through the live engine view', () => {
    assert.match(browser, /engineView\.SourceChanged \+= \(_, _\) => tab\.Owner\.OnSourceChanged\(tab\);/);
    assert.match(browser, /engineView\.TitleChanged \+= \(_, _\) => tab\.Owner\.OnTitleChanged\(tab\);/);
    assert.match(browser, /engineView\.HistoryChanged \+= \(_, _\) => tab\.Owner\.OnHistoryChanged\(tab\);/);
    assert.match(browser, /if \(!AcceptTabEvents\(tab\) \|\| IsInternalUrl\(tab\.Url\)\) return;\s*tab\.Url = tab\.EngineView\.Source;/);
    assert.match(browser, /if \(AcceptTabEvents\(tab\) && Active == tab\) UpdateNavigationButtons\(\);/);
    assert.match(browser, /string title = tab\.EngineView\.DocumentTitle;/);
    assert.doesNotMatch(webView2Window, /web\.(?:SourceChanged|DocumentTitleChanged|HistoryChanged) \+=/);
    assert.match(webView2, /core\.SourceChanged \+= OnSourceChanged;/);
    assert.match(webView2, /core\.DocumentTitleChanged \+= OnTitleChanged;/);
    assert.match(webView2, /core\.HistoryChanged \+= OnHistoryChanged;/);
    const dispose = webView2.slice(webView2.indexOf('public void Dispose()'));
    assert.match(dispose, /core\.SourceChanged -= OnSourceChanged;[\s\S]*?core\.DocumentTitleChanged -= OnTitleChanged;[\s\S]*?core\.HistoryChanged -= OnHistoryChanged;/);
    assert.match(cefView, /DestroyWindowCore[\s\S]*?VeilCefDetachCallbacks\(NativeHandle, window\.Handle\);[\s\S]*?DestroyWindow\(window\.Handle\)/);
});


test('tab audio and favicon data use the engine boundary without external icon fetches', () => {
    assert.match(browser, /engineView\.FaviconChanged \+= async \(_, _\) => await tab\.Owner\.UpdateFavicon\(tab\);/);
    assert.match(browser, /engineView\.AudioStateChanged \+= \(_, _\) => tab\.Owner\.UpdateAudio\(tab\);/);
    assert.match(browser, /AcceptTabEvents\(tab\) && tab\.EngineView\.IsMuted is \{ \} muted\)\s*tab\.EngineView\.SetMuted\(!muted\)/);
    assert.match(motion, /long epoch = tab\.NavigationEpoch;\s*long request = \+\+tab\.FaviconRequest;[\s\S]*?await tab\.EngineView\.GetFaviconPngAsync\(\)[\s\S]*?if \(!AcceptTabEvents\(tab\) \|\| tab\.NavigationEpoch != epoch \|\| tab\.FaviconRequest != request\) return;/);
    assert.doesNotMatch(webView2Window, /web\.(?:FaviconChanged|IsMutedChanged|IsDocumentPlayingAudioChanged) \+=/);
    assert.match(webView2, /await core\.GetFaviconAsync\(CoreWebView2FaviconImageFormat\.Png\)/);
    assert.match(cefView, /IsDocumentPlayingAudio => null;/);
    assert.match(cefView, /GetFaviconPngAsync\(\) => Task\.FromResult<Stream\?>\(null\)/);
    assert.match(cefView, /VeilCefSetMuted\(NativeHandle, muted \? 1 : 0\);/);
});


test('process failure distinguishes page loss from shared session shutdown', () => {
    assert.match(browser, /engineView\.ProcessFailed \+= \(_, args\) => tab\.Owner\.OnProcessFailed\(tab, args\);/);
    assert.doesNotMatch(webView2Window, /web\.ProcessFailed \+=/);
    assert.match(webView2, /args\.ProcessFailedKind == CoreWebView2ProcessFailedKind\.BrowserProcessExited\s*\? EngineFailureScope\.BrowserSession : EngineFailureScope\.PageOrChildProcess/);
    assert.match(webView2, /core\.ProcessFailed \+= OnProcessFailed;/);
    assert.match(webView2.slice(webView2.indexOf('public void Dispose()')), /core\.ProcessFailed -= OnProcessFailed;/);
    const failed = browser.slice(browser.indexOf('private void OnProcessFailed'), browser.indexOf('private void OnDownloadStarting'));
    assert.match(failed, /if \(args\.Scope != EngineFailureScope\.BrowserSession\)[\s\S]*?AcceptTabEvents\(tab\) && Active == tab[\s\S]*?return;[\s\S]*?if \(closing \|\| session\.EngineLost\) return;\s*session\.EngineLost = true;/);
    assert.match(failed, /session\.EngineLost = true;[\s\S]*?session\.MainSession\.Save\(\)[\s\S]*?session\.IsEnding = true;[\s\S]*?session\.AllWindows\.ToList\(\)\) window\.Close\(\);/);
    assert.match(cefView, /new EngineProcessFailedEventArgs\(EngineFailureScope\.PageOrChildProcess, code\)/);
    assert.match(cefView, /DestroyWindowCore[\s\S]*?VeilCefDetachCallbacks\(NativeHandle, window\.Handle\);[\s\S]*?DestroyWindow\(window\.Handle\)/);
});


const engineContract = await readFile('apps/desktop/Engine/IBrowserEngine.cs', 'utf8');
const cefNative = await readFile('apps/cef-smoke/wpf.cc', 'utf8');
test('popup adoption is one-shot, same-engine and profile-bound; CEF denies unsupported popups', () => {
    assert.match(webView2, /using var deferral = args\.GetDeferral\(\);\s*args\.Handled = true;/);
    assert.match(webView2, /handler\.GetInvocationList\(\)\.Length != 1/);
    assert.match(webView2, /!ReferenceEquals\(view\.environmentForPopup, environment\)[\s\S]*?!string\.Equals\(view\.privateProfileForPopup, privateProfileName[\s\S]*?args\.NewWindow = destination;/);
    assert.match(webView2, /finally \{ request\.Complete\(\); \}/);
    assert.match(engineContract, /var callback = pending;\s*pending = null;\s*return callback\?\.Invoke\(target\) == true;/);
    assert.doesNotMatch(webView2Window, /web\.(?:NewWindowRequested|WindowCloseRequested) \+=/);
    assert.match(cefNative, /bool OnBeforePopup\([\s\S]*?\+\+state_->popups_blocked;\s*return true;/);
    assert.match(cefView, /NewWindowRequested \{ add \{ \} remove \{ \} \}/);
});


test('page fullscreen is an observed engine state scoped to the active live tab', () => {
    assert.match(browser, /engineView\.FullScreenChanged \+= \(_, _\) => tab\.Owner\.OnPageFullScreenChanged\(tab\);/);
    assert.match(browser, /if \(!AcceptTabEvents\(tab\) \|\| Active != tab \|\| IsInternalUrl\(tab\.Url\)\) return;\s*if \(tab\.EngineView\.IsPageFullScreen is \{ \} pageFullScreen\)\s*SetFullScreen\(pageFullScreen, page: true\);/);
    assert.match(browser, /fullScreen && fullScreenFromPage && Active\?\.EngineView\.IsPageFullScreen != true/);
    assert.doesNotMatch(webView2Window, /web\.ContainsFullScreenElementChanged \+=/);
    assert.match(webView2, /core\.ContainsFullScreenElementChanged \+= OnFullScreenChanged;/);
    assert.match(webView2.slice(webView2.indexOf('public void Dispose()')), /core\.ContainsFullScreenElementChanged -= OnFullScreenChanged;/);
    assert.match(cefNative, /void OnFullscreenModeChange\([\s\S]*?state_->page_fullscreen = fullscreen;\s*if \(state_->page_state_changed\) state_->page_state_changed\(4\);/);
    assert.match(cefView, /case 4: FullScreenChanged\?\.Invoke\(this, EventArgs\.Empty\); break;/);
});


test('navigation errors retain their native engine domain and never reinterpret CEF as WebView2', () => {
    assert.match(engineContract, /enum EngineErrorDomain \{ WebView2, Cef \}/);
    assert.match(engineContract, /Error => IsSuccess \|\| IsCanceled \? null : NativeResult;/);
    assert.match(webView2, /new EngineNavigationError\(EngineErrorDomain\.WebView2, \(int\)args\.WebErrorStatus,\s*args\.WebErrorStatus\.ToString\(\)\)/);
    assert.match(cefView, /new EngineNavigationError\(EngineErrorDomain\.Cef, code,/);
    assert.match(browser, /var errorStatus = args\.Error;\s*tab\.LastNavigationError = errorStatus;/);
    assert.doesNotMatch(browser, /\(CoreWebView2WebErrorStatus\)args\.NativeErrorCode/);
    assert.match(browser, /if \(tab\.UpgradedFrom is \{ \} plain && tab\.ExpectedUpgrade is null\)[\s\S]*?if \(!args\.IsSuccess\)\s*\{\s*ShowHttpsWarning/);
});


test('hard reload and find use narrow engine operations with stale result guards', () => {
    const hardReload = browser.slice(browser.indexOf('private async void HardReload()'), browser.indexOf('private void StopLoading()'));
    assert.match(hardReload, /await tab\.EngineView\.ReloadIgnoringCacheAsync\(\)/);
    assert.match(hardReload, /bool Current\(\) => Active == tab && CanUseTab\(tab\.Item\) &&\s*tab\.NavigationEpoch == navigation && permissionSelectionEpoch == selection;/);
    assert.match(hardReload, /if \(!Current\(\)\) return;\s*try \{ tab\.EngineView\.Reload\(\); \}/);
    assert.doesNotMatch(hardReload, /CoreWebView2|CallDevToolsProtocolMethodAsync/);
    assert.match(webView2, /CallDevToolsProtocolMethodAsync\("Page\.reload", "\{\\"ignoreCache\\":true\}"\)/);
    assert.match(webView2, /JsonSerializer\.Serialize\(query\)[\s\S]*?Core\.ExecuteScriptAsync\(\$"window\.find\(\{text\}, false, \{reverse\}, true\)"\)/);
    assert.match(cefView, /RequireView\(\);\s*CefNative\.VeilCefReloadIgnoringCache\(NativeHandle\)/);
    assert.match(cefNative, /VeilCefReloadIgnoringCache\(uint64_t id\) \{\s*const auto browser = LookupBrowser\(id\);\s*if \(browser\) browser->ReloadIgnoreCache\(\);/);
    assert.match(cefView, /FindTextAsync\(string query, bool backwards\) => Task\.FromResult<bool\?>\(null\)/);
    assert.match(browser, /null => "현재 엔진에서는 페이지 검색을 지원하지 않습니다."/);
});


test('CEF client owns view metadata and callback registration as one lifetime', () => {
    assert.match(cefNative, /struct ViewState \{[\s\S]*?const HWND parent;[\s\S]*?std::wstring title;[\s\S]*?std::wstring page_source[\s\S]*?BeforeBrowse before_browse;/);
    assert.match(cefNative, /explicit Client\(std::shared_ptr<ViewState> state\) : state_\(std::move\(state\)\)/);
    assert.match(cefNative, /if \(views\.size\(\) >= ViewLimit\(\) \|\| !parent \|\| !starting \|\| !completed \|\| !changed \|\| !failed \|\|[\s\S]*?\)\s*return 0;/);
    assert.match(cefNative, /if \(!created \|\| entry == views\.end\(\) \|\| entry->second\.state != state\) \{\s*RemoveView\(state\);\s*return 0;/);
    assert.match(cefNative, /void VeilCefDetachCallbacks\(uint64_t id, HWND parent\) \{\s*const auto state = LookupView\(id\);\s*if \(state && state->parent == parent\) \{\s*state->closing = true;\s*state->DetachCallbacks\(\);\s*if \(state->owned_transfer\) state->owned_transfer->Cancel\(\);/);
    assert.match(cefNative, /void OnBeforeClose\([\s\S]*?RemoveView\(state_\);/);
    assert.match(cefView, /VeilCefOpen\(hwnd, onStarting, onCompleted, onPageStateChanged, onRenderProcessFailed\)/);
    assert.doesNotMatch(cefView, /VeilCefSet(?:NavigationCallbacks|PageStateCallback|RenderProcessFailedCallback)/);
});


test('CEF view operations require a nonzero matching monotonic identity', () => {
    assert.match(cefNative, /LookupView\(uint64_t id\) \{\s*const auto entry = views\.find\(id\);\s*return entry == views\.end\(\) \? nullptr : entry->second\.state;/);
    assert.match(cefNative, /std::make_shared<ViewState>\(\+\+last_view_id, parent,/);
    assert.match(cefNative, /return state->id;/);
    assert.match(cefNative, /VeilCefResize\(uint64_t id, HWND parent\)[\s\S]*?state->parent == parent && browser/);
    assert.match(cefView, /NativeHandle = CefNative\.VeilCefOpen\(hwnd,/);
    assert.match(cefView, /VeilCefDetachCallbacks\(NativeHandle, window\.Handle\);\s*NativeHandle = 0;/);
    for (const name of ['Navigate','SetMuted','GoBack','GoForward','Reload','ReloadIgnoringCache','Stop','SetZoomLevel','TryClose','StartOwnedAttachment','ClickFixtureAttachment']) {
        assert.match(cefNative, new RegExp('VeilCef' + name + '\\(uint64_t id[^)]*\\) \\{\\s*const auto browser = LookupBrowser\\(id\\);'));
    }
});


test('CEF native registry reserves identities before creation and erases only the matching view', () => {
    assert.match(cefNative, /std::unordered_map<uint64_t, ViewEntry> views;/);
    assert.match(cefNative, /constexpr unsigned kDevelopmentViewLimit = 1;/);
    assert.match(cefNative, /views\.emplace\(state->id, ViewEntry\{state, nullptr\}\);[\s\S]*?CreateBrowserSync\([\s\S]*?const auto entry = views\.find\(state->id\);[\s\S]*?entry->second\.browser = created;/);
    assert.match(cefNative, /void RemoveView\(const std::shared_ptr<ViewState>& state\) \{\s*state->DetachCallbacks\(\);\s*const auto entry = views\.find\(state->id\);\s*if \(entry != views\.end\(\) && entry->second\.state == state\)\s*views\.erase\(entry\);/);
    assert.match(cefNative, /LookupBrowser\(uint64_t id\) \{\s*const auto entry = views\.find\(id\);\s*return entry == views\.end\(\) \? nullptr : entry->second\.browser;/);
    assert.doesNotMatch(cefNative, /CefRefPtr<CefBrowser> active_browser;|std::shared_ptr<ViewState> active_view;/);
});


test('CEF fixture observations and attachment handoff are scoped to the originating view', () => {
    assert.match(cefNative, /struct ViewState \{[\s\S]*?std::atomic<int> response_matches\{0\};[\s\S]*?std::atomic<int> attachment_filtered_bytes\{-1\};[\s\S]*?CefString armed_page_attachment;/);
    assert.match(cefNative, /FixtureResponseProbe\(std::shared_ptr<ViewState> state, bool attachment\)\s*:\s*state_\(std::move\(state\)\), attachment_\(attachment\)/);
    assert.match(cefNative, /new FixtureResponseProbe\(state_, false\)/);
    assert.match(cefNative, /new FixtureResponseProbe\(state_, true\)/);
    assert.match(cefNative, /state_->response_matches\.store/);
    assert.match(cefNative, /state_->attachment_filtered_bytes\.store/);
    assert.match(cefNative, /fixture_enabled && !state_->armed_page_attachment\.empty\(\) && url == state_->armed_page_attachment/);
    assert.match(cefNative, /if \(!state->page_attachment_intercepted \|\| url != state->armed_page_attachment\) return 0;/);
    for (const name of ['PopupsBlocked','DownloadsBlocked','BlobDownloadsBlocked','PermissionsDenied','MediaPermissionsDenied','ResponseMatches','AttachmentFilteredBytes','PageAttachmentIntercepted']) {
        assert.match(cefNative, new RegExp('VeilCef' + name + '\\(uint64_t id\\) \\{\\s*const auto state = LookupView\\(id\\);'));
        assert.match(cefView, new RegExp('VeilCef' + name + '\\(ulong id\\)'));
    }
    assert.match(cefNative, /constexpr unsigned kDevelopmentViewLimit = 1;/);
});


test('owned download requests have per-transfer lifetime and cancel before releasing callbacks', () => {
    assert.match(cefNative, /struct OwnedTransfer \{[\s\S]*?void Cancel\(\) \{\s*if \(!active\) return;\s*active = false;\s*canceled = true;\s*result = -1;\s*chunk = nullptr;\s*const auto pending = request;\s*request = nullptr;\s*if \(pending\) pending->Cancel\(\);/);
    assert.match(cefNative, /const std::weak_ptr<OwnedTransfer> transfer_;/);
    assert.match(cefNative, /void OnRequestComplete\([\s\S]*?const auto transfer = transfer_\.lock\(\);\s*if \(!transfer \|\| !transfer->active\) return;/);
    assert.match(cefNative, /void OnDownloadData\([\s\S]*?if \(!transfer->active \|\| !transfer->chunk\) return;[\s\S]*?if \(!transfer->active\) return;/);
    assert.match(cefNative, /state->owned_transfer = transfer;[\s\S]*?new OwnedAttachmentClient\(transfer\)/);
    assert.match(cefNative, /if \(state->owned_transfer\) state->owned_transfer->Cancel\(\);/);
    assert.match(cefNative, /VeilCefOwnedAttachmentResult\(uint64_t id\)[\s\S]*?state->owned_transfer->result/);
    assert.match(cefNative, /VeilCefCancelOwnedAttachment\(uint64_t id\)[\s\S]*?state->owned_transfer->Cancel\(\)/);
    assert.doesNotMatch(cefNative, /OwnedChunk owned_chunk|CefRefPtr<CefURLRequest> owned_request/);
});


test('CEF close is scoped to a view and host shutdown waits for the native registry', () => {
    assert.match(cefNative, /VeilCefClosed\(uint64_t id\) \{ return LookupView\(id\) \? 0 : 1; \}/);
    assert.match(cefNative, /VeilCefLiveViewCount\(\) \{\s*return static_cast<unsigned>\(views\.size\(\)\);/);
    assert.match(cefNative, /void VeilCefDetachCallbacks\([\s\S]*?state->closing = true;\s*state->DetachCallbacks\(\);[\s\S]*?if \(browser && !browser->GetHost\(\)->IsReadyToBeClosed\(\)\)\s*browser->GetHost\(\)->CloseBrowser\(true\);/);
    assert.match(cefNative, /bool OnBeforeBrowse\([\s\S]*?if \(state_->closing\) return true;/);
    assert.match(cefNative, /const bool ready = browser->GetHost\(\)->TryCloseBrowser\(\);\s*if \(ready\) state->closing = true;/);
    assert.match(cefNative, /while \(!views\.empty\(\) && GetTickCount64\(\) - drain_started < 5000\)[\s\S]*?if \(!views\.empty\(\)\) \{[\s\S]*?std::quick_exit\(27\);[\s\S]*?CefShutdown\(\);/);
    assert.match(cefView, /VeilCefLiveViewCount\(\)/);
});

const cefSmoke = await readFile('apps/cef-smoke/wpf/WpfSmoke.cs', 'utf8');
test('WPF fixture retains the original closing identity and waits for the vault task', () => {
    assert.match(cefSmoke, /window\.Closing \+= \(_, e\) =>\s*\{\s*if \(!closing\)\s*\{\s*closing = true;\s*closingHandle = browser\.NativeHandle;/);
    assert.match(cefSmoke, /CefNative\.VeilCefCancelOwnedAttachment\(closingHandle\);\s*vaultStream\?\.Complete\(false\);/);
    assert.match(cefSmoke, /if \(windowClosed && CefNative\.VeilCefClosed\(closingHandle\) != 0 &&\s*CefNative\.VeilCefLiveViewCount\(\) == 0 && \(vaultStream is null \|\| vaultStream\.Finished\.IsCompleted\) &&\s*\(parallelKeep is null \|\| parallelKeep\.Finished\.IsCompleted\) &&\s*\(parallelCancel is null \|\| parallelCancel\.Finished\.IsCompleted\)\)[\s\S]*?app\.Shutdown\(\);/);
});


test('CEF permits two views only in the synthetic fixture and proves independent close', () => {
    assert.match(cefNative, /constexpr unsigned kFixtureViewLimit = 2;/);
    assert.match(cefNative, /unsigned ViewLimit\(\) \{ return \(fixture_enabled \|\| tabbed_development_enabled\) \? kFixtureViewLimit : kDevelopmentViewLimit; \}/);
    assert.match(cefNative, /if \(views\.size\(\) >= ViewLimit\(\)/);
    assert.match(cefView, /privateProfileName is not null \|\| views\.Count >= CefNative\.VeilCefViewLimit\(\)/);
    assert.match(cefSmoke, /var second = \(CefDevelopmentView\)engine\.CreateView\(\);/);
    assert.match(cefSmoke, /secondaryHandle == browser\.NativeHandle \|\|[\s\S]*?VeilCefLiveViewCount\(\) != 2/);
    assert.match(cefSmoke, /secondaryWindowClosed && CefNative\.VeilCefClosed\(secondaryHandle\) != 0/);
    assert.match(cefSmoke, /VeilCefSetMuted\(secondaryHandle, 1\);[\s\S]*?VeilCefLiveViewCount\(\) != 1/);
});


test('CEF managed view reservations are reclaimed only after disposal and native closure', () => {
    assert.match(cefView, /views\.RemoveAll\(view => view\.IsSlotReleased\);[\s\S]*?views\.Add\(view\);/);
    assert.match(cefView, /IsSlotReleased => disposed && NativeHandle == 0 &&\s*\(retiredHandle == 0 \|\| CefNative\.VeilCefClosed\(retiredHandle\) != 0\)/);
    assert.match(cefView, /if \(NativeHandle != 0\) retiredHandle = NativeHandle;\s*CefNative\.VeilCefDetachCallbacks/);
    assert.match(cefView, /protected override void Dispose\(bool disposing\)\s*\{\s*base\.Dispose\(disposing\);\s*if \(disposing\) disposed = true;/);
    assert.match(cefView, /if \(disposed \|\| NativeHandle != 0 \|\| \(retiredHandle != 0 && CefNative\.VeilCefClosed\(retiredHandle\) == 0\)\)/);
    assert.match(cefSmoke, /var reservation = engine\.CreateView\(\);\s*bool reservedLimit = Rejects\(\(\) => engine\.CreateView\(\)\);\s*reservation\.Dispose\(\);/);
    assert.match(cefSmoke, /firstSecondaryHandle = secondaryHandle;[\s\S]*?OpenSecondary\("\/wpf-isolation-reopened"\)/);
    assert.match(cefSmoke, /secondaryHandle <= firstSecondaryHandle \|\| CefNative\.VeilCefClosed\(firstSecondaryHandle\) == 0/);
});


test('CEF parallel fixture cancels only its own stream and waits for both vault workers', () => {
    assert.match(cefNative, /const bool parallel = path == "\/wpf-owned-parallel-keep" \|\| path == "\/wpf-owned-parallel-cancel";/);
    assert.match(cefNative, /if \(parallel\) \{[\s\S]*?CefString\(&source\.origin\) != CefString\(&parts\.origin\)/);
    assert.match(cefSmoke, /VeilCefStartOwnedAttachment\(browser\.NativeHandle, keepUrl, keep\.ChunkCallback\)[\s\S]*?VeilCefStartOwnedAttachment\(secondaryHandle, cancelUrl, cancel\.ChunkCallback\)/);
    assert.match(cefSmoke, /parallelKeep!\.ReceivedBytes > 0 && parallelCancel!\.ReceivedBytes > 0/);
    assert.match(cefSmoke, /canceledByteCount = parallelCancel\.ReceivedBytes;\s*CefNative\.VeilCefCancelOwnedAttachment\(secondaryHandle\);\s*parallelCancel\.Complete\(false\);/);
    assert.match(cefSmoke, /parallelKeep!\.Complete\(true\);/);
    assert.match(cefSmoke, /kept\.ReceivedBytes != FixtureVault\.BodySize \|\| canceled\.ReceivedBytes != canceledByteCount/);
});


const cefTabbed = await readFile('apps/cef-smoke/wpf/TabbedDevelopmentHost.cs', 'utf8');
test('CEF opt-in tab shell keeps native views parented and routes commands to the selected view', () => {
    assert.match(cefSmoke, /GetEnvironmentVariable\("VEIL_CEF_WPF_TABS"\) == "1"/);
    assert.match(cefNative, /tabbed_development_enabled =\s*GetEnvironmentVariableW\(L"VEIL_CEF_WPF_TABS", tabs_mode, 2\) == 1 && tabs_mode\[0\] == L'1';/);
    assert.match(cefTabbed, /pages\.Children\.Add\(view\.Element\);\s*Select\(tab\);/);
    assert.match(cefTabbed, /ReferenceEquals\(item, active\) \? Visibility\.Visible : Visibility\.Hidden/);
    assert.match(cefTabbed, /private void NavigateAddress\(\) => WithActive\(view => view\.Navigate\(address\.Text\)\);/);
    assert.match(cefTabbed, /tab\.Closing && CefNative\.VeilCefReadyToClose\(tab\.ClosingId\) != 0[\s\S]*?tab\.View\.Dispose\(\);[\s\S]*?pages\.Children\.Remove/);
    assert.match(cefTabbed, /shuttingDown && tabs\.Count == 0 && retired\.Count == 0 && CefNative\.VeilCefLiveViewCount\(\) == 0/);
});


test('page fullscreen exit is a narrow adapter operation and window shutdown disposes pending views', () => {
    assert.match(engineContract, /Task<bool> TryExitPageFullScreenAsync\(\);/);
    assert.match(webView2, /public async Task<bool> TryExitPageFullScreenAsync\(\)[\s\S]*?if \(IsPageFullScreen != true\) return false;\s*await Core\.ExecuteScriptAsync\("document\.fullscreenElement && document\.exitFullscreen\(\)"\);/);
    assert.match(cefView, /TryExitPageFullScreenAsync\(\) => Task\.FromResult\(false\);/);
    assert.doesNotMatch(tabs, /CoreWebView2|ExecuteScriptAsync/);
    assert.match(tabs, /bool Current\(\) => AcceptTabEvents\(tab\) && Active == tab && tab\.NavigationEpoch == epoch;[\s\S]*?await tab\.EngineView\.TryExitPageFullScreenAsync\(\) && Current\(\)/);
    assert.match(browser, /initializingViews\.Add\(engineView\);\s*try\s*\{\s*Pages\.Children\.Add\(element\);\s*await engineView\.InitializeAsync\(\);/);
    assert.match(browser, /finally \{ initializingViews\.Remove\(engineView\); \}/);
    assert.match(browser, /if \(session\.IsPrivate\) DeletePrivateProfile\(\);[\s\S]*?foreach \(var view in ownedTabs\.Select\(tab => tab\.EngineView\)\.Concat\(initializingViews\)\.Distinct\(\)\.ToArray\(\)\)\s*view\.Dispose\(\);/);
});


test('site data inspection exposes only narrow read-only capability and refuses private profiles', () => {
    assert.match(engineContract, /internal interface IEngineSiteDataReader\s*\{\s*Task<IReadOnlyList<string>> GetCookieDomainsAsync\(\);[\s\S]*?Task<double\?> GetOriginStorageUsageAsync\(string origin\);/);
    assert.match(webView2, /: IBrowserEngineView, IEngineSiteDataReader/);
    assert.match(webView2, /RequireStandardSiteDataCore\(\)\s*\{\s*var core = Core;\s*if \(core\.Profile\.IsInPrivateModeEnabled\)\s*throw new InvalidOperationException/);
    assert.match(webView2, /var cookies = await core\.CookieManager\.GetCookiesAsync\(""\);\s*if \(!ReferenceEquals\(core, RequireStandardSiteDataCore\(\)\)\)[\s\S]*?return cookies\.Select\(cookie => cookie\.Domain\)\.Where\(SiteDataPolicy\.ValidDomain\)\.ToArray\(\);/);
    assert.match(webView2, /if \(SiteDataPolicy\.Origin\(origin\) != origin\)[\s\S]*?CallDevToolsProtocolMethodAsync\("Storage\.getUsageAndQuota",[\s\S]*?if \(!ReferenceEquals\(core, RequireStandardSiteDataCore\(\)\)\)/);
    assert.match(webView2, /usage\.ValueKind == System\.Text\.Json\.JsonValueKind\.Number &&\s*usage\.TryGetDouble\(out double bytes\) && double\.IsFinite\(bytes\) && bytes >= 0\s*\? bytes : null;/);
    assert.match(cefView, /: HwndHost, IBrowserEngineView, IEngineSiteDataReader/);
    assert.match(siteData, /source\.EngineView is not IEngineSiteDataReader reader \|\|\s*source\.EngineView is not IEngineSiteDataWriter writer \|\|\s*source\.View\.CoreWebView2 is not \{ \} web \|\| web\.Profile\.IsInPrivateModeEnabled/);
    assert.match(siteData, /await reader\.GetOriginStorageUsageAsync\(origin\);\s*if \(!SourceStillValid\(\) \|\| permissionSelectionEpoch != confirmedSelectionEpoch\) return;/);
    assert.doesNotMatch(siteData, /Storage\.getUsageAndQuota|cookie\.Value/);
    assert.doesNotMatch(siteData, /CookieManager|CallDevToolsProtocolMethodAsync|ClearBrowsingDataAsync/);
});


test('site data mutations are exact-scope capabilities with authorization rechecked before native operations', () => {
    assert.match(engineContract, /internal interface IEngineSiteDataWriter[\s\S]*?Task<int\?> DeleteCookieDomainAsync\(string domain, Func<bool> stillAuthorized\);[\s\S]*?Task<bool> ClearOriginStorageAsync\(string origin, Func<bool> stillAuthorized\);[\s\S]*?Task<bool> ClearProfileDataAsync\(EngineProfileDataKinds kinds, Func<bool> stillAuthorized\);/);
    assert.doesNotMatch(cefView, /IEngineSiteDataWriter/);
    assert.match(webView2, /if \(!SiteDataPolicy\.ValidDomain\(domain\)\)[\s\S]*?var fresh = await core\.CookieManager\.GetCookiesAsync\(""\);\s*if \(!stillAuthorized\(\)\) return null;\s*if \(!ReferenceEquals\(core, RequireStandardSiteDataCore\(\)\)\)/);
    assert.match(webView2, /SiteDataPolicy\.SameCookieDomain\(cookie\.Domain, domain\)\) continue;\s*core\.CookieManager\.DeleteCookie\(cookie\);/);
    assert.match(webView2, /if \(string\.IsNullOrEmpty\(origin\) \|\| SiteDataPolicy\.Origin\(origin\) != origin\)[\s\S]*?if \(!stillAuthorized\(\)\) return false;\s*var core = RequireStandardSiteDataCore\(\);\s*await core\.CallDevToolsProtocolMethodAsync\("Storage\.clearDataForOrigin",\s*System\.Text\.Json\.JsonSerializer\.Serialize\(new \{ origin, storageTypes = SiteDataPolicy\.OriginStorageTypes \}\)\)/);
    assert.match(webView2, /if \(kinds == EngineProfileDataKinds\.None \|\| \(kinds & ~allowed\) != 0\)[\s\S]*?if \(!stillAuthorized\(\)\) return false;\s*var core = RequireStandardSiteDataCore\(\);/);
    assert.match(webView2, /nativeKinds \|= CoreWebView2BrowsingDataKinds\.Cookies;/);
    assert.match(webView2, /nativeKinds \|= CoreWebView2BrowsingDataKinds\.AllDomStorage;/);
    assert.match(webView2, /nativeKinds \|= CoreWebView2BrowsingDataKinds\.DiskCache;/);
    assert.doesNotMatch(webView2, /CoreWebView2BrowsingDataKinds\.(AllProfile|Settings)/);
    assert.match(siteData, /if \(!await writer\.ClearOriginStorageAsync\(origin, StillAuthorized\)\) return;/);
    assert.match(siteData, /if \(!await writer\.ClearProfileDataAsync\(kinds, StillAuthorized\)\) return;/);
    assert.match(siteData, /if \(!StillAuthorized\(\)\) return;\s*remaining = \(await reader\.GetCookieDomainsAsync\(\)\)\.Count/);
});


test('CEF cookie domain enumeration is a bounded read-only fixture operation tied to view lifetime', () => {
    assert.match(cefNative, /class CookieDomainVisitor final : public CefCookieVisitor[\s\S]*?~CookieDomainVisitor\(\) override \{ scan_->done\.store\(true, std::memory_order_release\); \}/);
    assert.match(cefNative, /delete_cookie = false;\s*const auto domain = CefString\(&cookie\.domain\)\.ToWString\(\);/);
    assert.match(cefNative, /if \(!fixture_enabled \|\| !state \|\| !browser \|\| state->closing \|\| state->cookie_scan\) return 0;/);
    assert.match(cefNative, /browser->GetHost\(\)->GetRequestContext\(\);[\s\S]*?context->GetCookieManager\(nullptr\)[\s\S]*?VisitAllCookies\(new CookieDomainVisitor\(scan\)\)/);
    assert.match(cefNative, /if \(!scan->done\.load\(std::memory_order_acquire\)\) return 0;/);
    assert.match(cefView, /ulong id = NativeHandle;[\s\S]*?VeilCefStartCookieDomainScan\(id\)[\s\S]*?if \(NativeHandle != id\) throw new InvalidOperationException/);
    assert.match(cefView, /finally \{ CefNative\.VeilCefEndCookieDomainScan\(id\); \}/);
    assert.match(cefView, /GetOriginStorageUsageAsync\(string origin\) => Task\.FromResult<double\?>\(null\);/);
    assert.match(cefTabbed, /emptyDomains\.Result\.Count != 0/);
    assert.match(cefTabbed, /firstDomains\.Result\.Count != 2 \|\| secondDomains\.Result\.Count != 2/);
    assert.match(cefTabbed, /VeilCefCookieDomainScanStatus\(secondId\) != -1/);
});
