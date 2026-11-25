// SPDX-License-Identifier: MPL-2.0
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
const browser = await readFile('apps/desktop/BrowserWindow.xaml.cs', 'utf8');
const tabs = await readFile('apps/desktop/BrowserWindow.Tabs.cs', 'utf8');
const motion = await readFile('apps/desktop/BrowserWindow.Motion.cs', 'utf8');
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
    const popup = browser.slice(browser.indexOf('private async void OnNewWindowRequested'), browser.indexOf('private void UpdateAudio'));
    assert.match(popup, /long epoch = opener\.NavigationEpoch;[\s\S]*?await OpenTab\([\s\S]*?if \(CanUseTab\(opener\.Item\) && opener\.NavigationEpoch == epoch && CanUseTab\(popup\.Item\)\)\s*args\.NewWindow = popup\.View\.CoreWebView2;/);
    assert.match(popup, /else if \(!closing && CanUseTab\(popup\.Item\)\)\s*await CloseTabAsync\(popup\.Item\);/);
});


const session = await readFile('apps/desktop/BrowserSession.cs', 'utf8');
test('tab transfer keeps source alive until adoption and defers intermediate snapshots', () => {
    const transfer = tabs.slice(tabs.indexOf('private bool TransferTabTo'), tabs.indexOf('private void AttachTab'));
    assert.match(transfer, /session\.TabTransferDepth\+\+;[\s\S]*?DetachTab\(item\);[\s\S]*?target\.AttachTab\(item, index\);\s*if \(Tabs\.Items\.Count == 0\) Close\(\)/);
    assert.match(transfer, /catch \(Exception error\) when \(RecoverableFailure\.Is\(error\)\)[\s\S]*?tab\.Owner = this;[\s\S]*?Pages\.Children\.Add\(tab\.View\)[\s\S]*?Tabs\.Items\.Insert\(TabInsertionIndex\(tab\.Pinned, originalIndex\), item\)/);
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
