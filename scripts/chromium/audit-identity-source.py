# SPDX-License-Identifier: MPL-2.0
"""Read-only audit of fixed upstream text files. No Chromium code execution."""
import base64
import hashlib
import json
import pathlib
import re
import urllib.error
import urllib.request
import concurrent.futures

ROOT = pathlib.Path(__file__).resolve().parents[2]
MAX_BYTES = 8 * 1024 * 1024
BASE = "https://chromium.googlesource.com/chromium/src/+/"
FILES = {
    "chrome/install_static/install_modes.h": ("d2a8f592a71cca272e8d86c547803bf34a49921a", r"kCompanyPathName|kProductPathName|kSafeBrowsingName|kInstallModes|chromium_install_modes"),
    "chrome/install_static/install_util.cc": ("f7798f7da79cee7e0a5b1e2170921820ecd31707", r"kProductPathName|GetUserDataDirectory|install_suffix|base_app_name"),
    "chrome/install_static/chromium_install_modes.h": (None, r"kCompanyPathName|kProductPathName|kSafeBrowsingName|kInstallModes|Chromium|appid|app_guid|appguid|prog_id|base_app_name|suffix"),
    "chrome/install_static/user_data_dir.cc": (None, r"GetUserDataDirectory|user-data-dir|User Data|kProductPathName|policy|Policy"),
    "chrome/common/chrome_paths_win.cc": (None, r"GetDefaultUserDataDirectory|GetUserDataDirectory|DIR_USER_DATA|install_static"),
    "chrome/browser/process_singleton_win.cc": (None, r"user_data_dir|UserData|mutex|Mutex|window_name"),
    "chrome/app/chromium_strings.grd": ("dea90cda0be32f0d6c94ce7950a00970aded6299", r'name="IDS_(PRODUCT_NAME|SHORT_PRODUCT_NAME|ABOUT_VERSION|PRODUCT_NAME_BETA)[^"]*"'),
    "chrome/app/resources/chromium_strings_ko.xtb": (None, r"Chromium|크로미엄"),
}

def inspect_blob(path, raw, expected, pattern):
    blob = hashlib.sha1(b"blob " + str(len(raw)).encode("ascii") + b"\0" + raw).hexdigest()
    if expected is not None and blob != expected:
        raise ValueError("Pinned source blob mismatch: " + path)
    lines = raw.decode("utf-8", errors="strict").splitlines()
    matches = [{"line": i, "text": line.strip()[:240]} for i, line in enumerate(lines, 1)
               if re.search(pattern, line)]
    return {"path": path, "status": "SOURCE_OBSERVED", "blob": blob,
            "sha256": hashlib.sha256(raw).hexdigest(), "lineCount": len(lines),
            "matchCount": len(matches), "matches": matches[:60],
            "matchesTruncated": len(matches) > 60}

def fetch_one(revision, path, expected, pattern):
    try:
        with urllib.request.urlopen(BASE + revision + "/" + path + "?format=TEXT", timeout=25) as response:
            if not response.geturl().startswith("https://chromium.googlesource.com/"):
                raise ValueError("Unexpected redirect")
            encoded = response.read(MAX_BYTES + 1)
        if len(encoded) > MAX_BYTES:
            raise ValueError("Source response too large")
        return inspect_blob(path, base64.b64decode(encoded.strip(), validate=True), expected, pattern)
    except (OSError, ValueError) as error:
        # Missing optional locations are explicit unknowns, never presumed safe.
        return {"path": path, "status": "NOT_OBSERVED", "required": expected is not None,
                "error": type(error).__name__, "detail": str(error)[:200]}

def main():
    pin = json.loads((ROOT / "chromium/candidate.json").read_text(encoding="utf-8"))
    revision = pin["revision"]
    if revision != "1de57fa19722fa6d6841d4f596c394d015580656":
        raise ValueError("Review audit paths and expected blob IDs for the new revision")
    with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:
        futures = [pool.submit(fetch_one, revision, path, expected, pattern)
                   for path, (expected, pattern) in FILES.items()]
        rows = [future.result() for future in futures]
    missing = [row["path"] for row in rows if row["status"] != "SOURCE_OBSERVED"]
    result = {"status": "PARTIAL_SOURCE_SURVEY" if missing else "SOURCE_SURVEY_ONLY",
              "revision": revision, "missing": missing, "files": rows,
              "profileIsolation": "NOT RUN", "brandingRuntime": "NOT RUN", "releaseApproved": False}
    print(json.dumps(result, ensure_ascii=True, indent=2))
    if any(row.get("required") for row in rows):
        raise SystemExit("Required source evidence unavailable")

if __name__ == "__main__":
    main()
