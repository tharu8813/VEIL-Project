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
    assert.match(siteData, /GetCookiesAsync\(""\)\)[\s\S]*?if \(!SourceStillValid\(\) \|\| permissionSelectionEpoch != selectionEpoch/);
    assert.match(siteData, /!SourceStillValid\(\) \|\| session\.ActiveDownloads\.Count != 0 \|\| !SiteDataPolicy\.ValidSelection\(selection\)/);
    assert.match(siteData, /var fresh = await web\.CookieManager\.GetCookiesAsync\(""\);\s*if \(!SourceStillValid\(\) \|\| permissionSelectionEpoch != confirmedSelectionEpoch/);
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
    assert.match(cefNative, /bool OnBeforePopup\([\s\S]*?\+\+popups_blocked;\s*return true;/);
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
    assert.match(cefNative, /if \(active_browser \|\| active_view \|\| !parent \|\| !starting \|\| !completed \|\| !changed \|\| !failed \|\|[\s\S]*?\)\s*return 0;/);
    assert.match(cefNative, /if \(!active_browser\) \{\s*state->DetachCallbacks\(\);\s*if \(active_view == state\) active_view\.reset\(\);/);
    assert.match(cefNative, /void VeilCefDetachCallbacks\(uint64_t id, HWND parent\) \{\s*const auto state = LookupView\(id\);\s*if \(state && state->parent == parent\)\s*state->DetachCallbacks\(\);/);
    assert.match(cefNative, /void OnBeforeClose\([\s\S]*?state_->DetachCallbacks\(\);\s*if \(active_view == state_\) \{\s*active_browser = nullptr;\s*active_view\.reset\(\);/);
    assert.match(cefView, /VeilCefOpen\(hwnd, onStarting, onCompleted, onPageStateChanged, onRenderProcessFailed\)/);
    assert.doesNotMatch(cefView, /VeilCefSet(?:NavigationCallbacks|PageStateCallback|RenderProcessFailedCallback)/);
});


test('CEF view operations require a nonzero matching monotonic identity', () => {
    assert.match(cefNative, /LookupView\(uint64_t id\) \{\s*return id != 0 && active_view && active_view->id == id \? active_view : nullptr;/);
    assert.match(cefNative, /std::make_shared<ViewState>\(\+\+last_view_id, parent,/);
    assert.match(cefNative, /return state->id;/);
    assert.match(cefNative, /VeilCefResize\(uint64_t id, HWND parent\)[\s\S]*?state->parent == parent && browser/);
    assert.match(cefView, /NativeHandle = CefNative\.VeilCefOpen\(hwnd,/);
    assert.match(cefView, /VeilCefDetachCallbacks\(NativeHandle, window\.Handle\);\s*NativeHandle = 0;/);
    for (const name of ['Navigate','SetMuted','GoBack','GoForward','Reload','ReloadIgnoringCache','Stop','SetZoomLevel','TryClose','StartOwnedAttachment','ClickFixtureAttachment']) {
        assert.match(cefNative, new RegExp('VeilCef' + name + '\\(uint64_t id[^)]*\\) \\{\\s*const auto browser = LookupBrowser\\(id\\);'));
    }
});
