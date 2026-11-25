# SPDX-License-Identifier: MPL-2.0
"""Review exact startup/COM source spans, never execute or modify Chromium."""
import hashlib
import json
import pathlib
from pinned_gitiles import fetch_pinned_blob

ROOT = pathlib.Path(__file__).resolve().parents[2]
REV = "1de57fa19722fa6d6841d4f596c394d015580656"
# Exact blob IDs observed in the prior source audit. Spans are review windows,
# not a complete call graph. All required anchors must occur in their window.
SOURCES = (
    ("chrome/install_static/user_data_dir.cc", "6b231841bf68c1abd8087bb1c0ea70f3ef3c9ece",
     ((27, 55, ("GetUserDataDirFromRegistryPolicyIfSet", "ExpandPathVariables")),
      (74, 105, ("LOCALAPPDATA", "GetTempPath")),
      (106, 165, ("GetUserDataDirFromRegistryPolicyIfSet",)))),
    ("chrome/app/chrome_main_delegate.cc", "33a3b3844ad4839eb87c15c7aef0c956abd9f98e",
     ((585, 610, ("ChromeProcessSingleton::CreateInstance",)),
      (655, 674, ("GetUserDataDirectoryThunk", "OverrideAndCreateIfNeeded", "kUserDataDir")))),
    ("chrome/browser/chrome_browser_main.cc", "9aa6f338c73e80ee878c068db1811230ee3e6339",
     ((1160, 1185, ("chrome::DIR_USER_DATA",)),)),
    ("chrome/install_static/chromium_install_modes.h", "a6d969df56d13d94eaa968934f909d6fd23b5f2f",
     ((66, 99, ("toast_activator_clsid", "elevator_clsid", "elevator_iid", "tracing_service_clsid", "tracing_service_iid")),)),
    ("chrome/elevation_service/elevation_service_idl.idl", "255ba3984953c4414daa82091fdafcf5828aad5d",
     ((125, 160, ("IElevatorChromium", "IElevatorChrome", "uuid(")),)),
)

def inspect_spans(raw, spans):
    lines = raw.decode("utf-8", errors="strict").splitlines()
    result = []
    previous = 0
    for first, last, anchors in spans:
        if not (previous < first <= last <= len(lines)) or last - first >= 80 or not anchors:
            raise ValueError("Invalid review window")
        window = lines[first - 1:last]
        for anchor in anchors:
            if not anchor or not any(anchor in line for line in window):
                raise ValueError("Expected source anchor missing: " + anchor)
        result.append({"first": first, "last": last, "lines": [
            {"line": first + i, "text": line} for i, line in enumerate(window)
        ]})
        previous = last
    return result

def main():
    pin = json.loads((ROOT / "chromium/candidate.json").read_text(encoding="utf-8"))
    if pin["revision"] != REV:
        raise ValueError("Review all source spans for the new revision")
    rows = []
    for path, blob, spans in SOURCES:
        raw = fetch_pinned_blob(REV, path, blob, 8 * 1024 * 1024)
        rows.append({"path": path, "blob": blob, "sha256": hashlib.sha256(raw).hexdigest(),
                     "windows": inspect_spans(raw, spans)})
    print(json.dumps({"status": "SOURCE_WINDOWS_OBSERVED_ONLY", "revision": REV, "files": rows,
                      "profileIsolation": "NOT RUN", "comRegistration": "NOT RUN",
                      "build": "NOT RUN", "runtime": "NOT RUN", "releaseApproved": False},
                     ensure_ascii=True, indent=2))

if __name__ == "__main__":
    main()
