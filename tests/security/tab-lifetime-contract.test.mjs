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
