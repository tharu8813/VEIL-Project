# 25. Local omnibox (Stage 2-3)

- Rank exact host, host/URL prefix, title prefix, substring, then all-token matches. Match host without www. Deduplicate canonical URL and preserve bookmark title/source on overlap. Stable tie ordering.
- Show bookmark/history origin. Up/down past the list ends restores unselected typed text; Ctrl+click opens a suggestion in a new tab. Revalidate active window/tab and local policy before opening.
- Explicit search syntax: `? words` uses default engine even for a domain-looking query; `@google words`, `@bing words`, `@naver words`, `@duckduckgo words` select a provider for one submission. Blank explicit searches do not navigate. No remote completion; no change to stored defaults.
- Existing exclusions (credentials, query strings, fragments, non-web URLs) remain. Search uses fixed HTTPS provider URLs only. Bound suggestions to eight results, 20,000 bookmarks, 4,000 history entries, 12 words, 256 input characters. UI latency on large libraries remains to be measured.
- Synthetic ranking, provider and exclusion smoke assertions added. Actual Windows IME, keyboard/mouse, layout and performance verification NOT RUN. No release gate changes.
