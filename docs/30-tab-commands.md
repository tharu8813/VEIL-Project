# 30. Tab commands (Stage 2-8)

- Right-click targets the chosen tab without switching to it first. Shift+F10 / Menu key opens a focused tab's menu; native keyboard and mouse smoke still required.
- Add left-side bulk close and a submenu for move to a new or existing regular window. Relative scopes are computed when clicked, not when the menu was opened. Bulk close never closes pinned tabs and rechecks each item.
- Revalidate tab membership, ownership, close animation and target window at action time. Session identity must match before attaching a live tab. Private windows cannot transfer tabs into regular windows or other private sessions.
- Number shortcuts ignore missing positions instead of clamping to the last tab. Next/previous cycling skips closing tabs. Deferred browser commands check active window and lifecycle again.
- Keep at most 50 recently closed web tabs in RAM; serialize restore per session. No new persistence, private closed-tab history or OS clipboard interaction. Restore opens a new page, not a suspended live document.
- Synthetic desktop smoke extends move, isolation and numbered/cycle checks. Actual Windows menu, keyboard delivery from WebView, drag, race and stage-2 batch validation NOT RUN. No release gate change.
