# SPDX-License-Identifier: MPL-2.0
"""Read exact pinned startup call sites; never run or patch upstream code."""
import json
import pathlib
import re
from pinned_gitiles import fetch_pinned_blob

ROOT = pathlib.Path(__file__).resolve().parents[2]
REV = "1de57fa19722fa6d6841d4f596c394d015580656"
SOURCES = (
    ("chrome/install_static/user_data_dir.cc", "6b231841bf68c1abd8087bb1c0ea70f3ef3c9ece",
     r"GetUserDataDirectory|g_user_data_dir|g_temp_user_data_dir_created_for_headless|GetCommandLineSwitch|RecursiveDirectoryCreate|GetDefaultUserDataDirectory", ("GetUserDataDirectoryImpl",)),
    ("chrome/install_static/user_data_dir.h", "8968f85a5fb9fa784f4cb25fb9f009f12bf26a6e",
     r"GetUserDataDirectory|Headless|invalid_supplied_directory", ("GetUserDataDirectory",)),
    ("chrome/install_static/install_util.cc", "f7798f7da79cee7e0a5b1e2170921820ecd31707",
     r"GetUserDataDirectory|GetCommandLineSwitch|GetCommandLineSwitchValue|MakeAbsoluteFilePath", ("GetUserDataDirectory",)),
    ("chrome/app/chrome_main_delegate.cc", "33a3b3844ad4839eb87c15c7aef0c956abd9f98e",
     r"InitializeUserDataDir|GetUserDataDirectoryThunk|DIR_USER_DATA|PreSandboxStartup|InitializeCrash|CrashReporter|AcquireProcessSingleton", ("GetUserDataDirectoryThunk", "InitializeUserDataDir")),
    ("chrome/common/chrome_paths.cc", "4448f86df86f764874539303e1c002e3c85f7e66",
     r"DIR_USER_DATA|GetDefaultUserDataDirectory|CreateDirectory", ("DIR_USER_DATA",)),
    ("chrome/common/chrome_paths_win.cc", "9ab342785936f8e9c8f64ac5088dedcf276058ce",
     r"GetDefaultUserDataDirectory|GetUserDataDirectory|DIR_USER_DATA", ("GetDefaultUserDataDirectory",)),
)
CONTEXT = 6
MAX_LINES = 640


def collect(raw, pattern, anchors):
    lines = raw.decode("utf-8", errors="strict").splitlines()
    if not all(any(anchor in line for line in lines) for anchor in anchors):
        raise ValueError("Mandatory caller anchor missing")
    regex = re.compile(pattern)
    hits = [i for i, line in enumerate(lines) if regex.search(line)]
    if not hits:
        raise ValueError("No caller matches")
    windows = []
    for i in hits:
        start, end = max(0, i - CONTEXT), min(len(lines), i + CONTEXT + 1)
        if windows and start <= windows[-1][1]:
            windows[-1] = (windows[-1][0], max(end, windows[-1][1]))
        else:
            windows.append((start, end))
    if sum(end - start for start, end in windows) > MAX_LINES:
        raise ValueError("Caller evidence exceeds bound; narrow the search")
    return {"hitCount": len(hits), "hitLines": [i + 1 for i in hits],
            "windows": [{"first": start + 1, "last": end,
                         "lines": [{"line": i + 1, "text": lines[i]}
                                   for i in range(start, end)]}
                        for start, end in windows]}


def main():
    pin = json.loads((ROOT / "chromium/candidate.json").read_text(encoding="utf-8"))
    if pin["revision"] != REV:
        raise ValueError("Re-review startup call sites for the new revision")
    rows = []
    for path, blob, pattern, anchors in SOURCES:
        raw = fetch_pinned_blob(REV, path, blob, 8 * 1024 * 1024)
        rows.append({"path": path, "blob": blob, **collect(raw, pattern, anchors)})
    print(json.dumps({"status": "PINNED_CALLER_WINDOWS_ONLY",
                      "revision": REV, "files": rows,
                      "completeCallGraph": False, "failureStopsAllProcesses": "NOT RUN",
                      "crashReporting": "NOT RUN", "profileIsolation": "NOT RUN",
                      "build": "NOT RUN", "runtime": "NOT RUN", "releaseApproved": False},
                     ensure_ascii=True, indent=2))


if __name__ == "__main__":
    main()
