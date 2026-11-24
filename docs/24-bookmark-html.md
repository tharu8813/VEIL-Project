# 24. Bookmark HTML interchange (Stage 2-2)

Explicit local file selection only. Supported format: UTF-8 Netscape Bookmark HTML, as exported by common browsers (actual Chrome/Edge/Firefox sample compatibility NOT RUN). No direct browser-profile access, remote requests, HTML rendering, imported scripts, icons or other active data.

Import appends a validated folder hierarchy and HTTP(S) links, skips unsupported schemes, credential-bearing URLs and canonical duplicates. Limits: 8 MiB, 20,000 items, 16 folder levels. Invalid or unsupported input does not change the saved data; save failure restores both lists.

Export creates a new user-selected HTML file after a plaintext warning. Names and addresses are escaped. No overwrite, no history/cookies/passwords. Existing protected state is unchanged.

Synthetic parser/entity/duplicate/unsafe URL and round-trip assertions added to desktop smoke. Windows UI, real browser export samples, file failure paths, and protected-volume VM checks remain NOT RUN. No security release gate changes.
