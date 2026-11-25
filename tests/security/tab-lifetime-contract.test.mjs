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
