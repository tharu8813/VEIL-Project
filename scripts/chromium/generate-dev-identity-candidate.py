# SPDX-License-Identifier: MPL-2.0
"""Generate review-only Windows development identity changes, never install or launch."""
import difflib
import hashlib
import json
import pathlib
import re
import subprocess
import tempfile
from pinned_gitiles import fetch_pinned_blob

ROOT = pathlib.Path(__file__).resolve().parents[2]
REV = "1de57fa19722fa6d6841d4f596c394d015580656"
REL = "chrome/install_static/chromium_install_modes.h"
BLOB = "a6d969df56d13d94eaa968934f909d6fd23b5f2f"
# Development-only namespace, not an approved installer/application identity.
# Do not alter Safe Browsing, empty Google Update app GUID, COM GUIDs, or policy.
CHANGES = (
    (r"\bkProductPathName\[\]\s*=\s*L", "Chromium", "VEIL Chromium Dev"),
    (r"\.base_app_name\s*=\s*L", "Chromium", "VEIL Chromium Dev"),
    (r"\.base_app_id\s*=\s*L", "Chromium", "VEILChromiumDev"),
    (r"\.browser_prog_id_prefix\s*=\s*L", "ChromiumHTM", "VEILDevHTM"),
    (r"\.pdf_prog_id_prefix\s*=\s*L", "ChromiumPDF", "VEILDevPDF"),
    (r"\.browser_prog_id_description\s*=\s*L", "Chromium HTML Document", "VEIL Development HTML Document"),
    (r"\.pdf_prog_id_description\s*=\s*L", "Chromium PDF Document", "VEIL Development PDF Document"),
    (r"\.direct_launch_url_scheme\s*=\s*", "chromium", "veil-chromium-dev"),
    (r"\.active_setup_guid\s*=\s*L", "{7D2B3E1D-D096-4594-9D8F-A6667F12E0AC}", "{C0D85D7A-6F11-49E9-A4B1-2D8F6C11B7E3}"),
)


def rewrite(raw):
    source = raw.decode("utf-8", errors="strict")
    result = source
    for prefix, old, new in CHANGES:
        pattern = "(" + prefix + r'")' + re.escape(old) + r'(")'
        result, count = re.subn(pattern, lambda m: m[1] + new + m[2], result)
        if count != 1:
            raise ValueError("Expected exactly one matching identity: " + prefix)
    return result.encode("utf-8")


def main():
    pin = json.loads((ROOT / "chromium/candidate.json").read_text(encoding="utf-8"))
    if pin["revision"] != REV:
        raise ValueError("Review generator for the new upstream revision")
    original = fetch_pinned_blob(REV, REL, BLOB, 65536)
    blob = BLOB
    modified = rewrite(original)
    diff = "".join(difflib.unified_diff(original.decode().splitlines(keepends=True),
                                       modified.decode().splitlines(keepends=True),
                                       fromfile="a/" + REL, tofile="b/" + REL))
    patch = ("diff --git a/" + REL + " b/" + REL + "\n" + diff).encode()
    with tempfile.TemporaryDirectory(prefix="veil-identity-candidate-") as tmp:
        target = pathlib.Path(tmp) / REL
        target.parent.mkdir(parents=True)
        target.write_bytes(original)
        subprocess.run(["git", "-C", tmp, "init", "-q"], check=True, timeout=20)
        for check in (["--check"], []):
            subprocess.run(["git", "-C", tmp, "apply", *check, "--whitespace=error-all", "-"],
                           input=patch, check=True, timeout=20)
        if target.read_bytes() != modified:
            raise ValueError("Patch result mismatch")
    print(json.dumps({"status": "DEVELOPMENT_IDENTITY_REVIEW_ONLY", "revision": REV, "blob": blob,
                      "patchSha256": hashlib.sha256(patch).hexdigest(), "changedFields": len(CHANGES),
                      "registered": False, "defaultProfileRuntime": "NOT RUN",
                      "policyOverride": "NOT RUN", "comIdentity": "NOT RUN",
                      "installer": "NOT RUN", "protocolRuntime": "NOT RUN",
                      "activeSetupRuntime": "NOT RUN", "releaseApproved": False}))
    print("BEGIN REVIEW DIFF")
    print(patch.decode(), end="")
    print("END REVIEW DIFF")


if __name__ == "__main__":
    main()
