# 24. Bookmark HTML interchange (Stage 2-2)

Explicit local file selection only. Supported format: UTF-8 Netscape Bookmark HTML, as exported by common browsers (actual Chrome/Edge/Firefox sample compatibility NOT RUN). No direct browser-profile access, remote requests, HTML rendering, imported scripts, icons or other active data.

Import appends a validated folder hierarchy and HTTP(S) links, skips unsupported schemes, credential-bearing URLs and canonical duplicates. Limits: 8 MiB, 20,000 items, 16 folder levels. Invalid or unsupported input does not change the saved data; save failure restores both lists.

Export creates a new user-selected HTML file after a plaintext warning. Names and addresses are escaped. No overwrite, no history/cookies/passwords. Existing protected state is unchanged.

Synthetic parser/entity/duplicate/unsafe URL and round-trip assertions added to desktop smoke. Windows UI, real browser export samples, file failure paths, and protected-volume VM checks remain NOT RUN. No security release gate changes.

후속 자동화: Debug 전용 파일 선택 훅으로 가져오기 취소·활성 HTML 거부·저장 실패 롤백·중복/위험 URL 제외·평문 내보내기 확인 및 취소·왕복·덮어쓰기 거부를 `p6-bookmark-folders`에 추가했다. Release는 기존 네이티브 파일 선택 및 확인을 유지한다. Windows 실행과 실제 브라우저 샘플은 NOT RUN.
