# 34. Download overview (Stage 3-3)

- Ctrl+J opens a native management tab with searchable name/status/source, state filter (all/stored/failed/blocked/cancelled/recovered), stable time/name/size ordering, 100 rows per page. Unknown sizes sort after known sizes; recovered items remain identified.
- Active transfers and cancel actions stay in the existing native progress panel. Actual stored-file operations (preview, integrity check, export, delete) stay in the existing authenticated vault manager; no alternate plaintext/export route.
- Stored records are shown only while the vault is unlocked and its opening gate is idle. Refresh on result updates and while visible; clear visual rows on unload/hide. No download metadata in WebView internal HTML. No new persistence.
- Up to 100 recent non-success outcomes in RAM (previously 20). Explicit clear removes those visible issue records and outstanding retry candidates, not vault files or test diagnostic results. Existing transient compatibility history bounded to 200. Popup still displays up to eight recent results.
- Native desktop smoke adds policy and route assertions. Real Windows search/filter, large-index performance, lock visibility, multi-window and cancellation regression NOT RUN. No release gate change.
