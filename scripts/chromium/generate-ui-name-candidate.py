# SPDX-License-Identifier: MPL-2.0
"""Generate a review-only GRIT name patch from the exact upstream blob."""
import argparse
import base64
import difflib
import hashlib
import json
import pathlib
import re
import subprocess
import tempfile
import urllib.request
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parents[2]
REV = "1de57fa19722fa6d6841d4f596c394d015580656"
REL = "chrome/app/chromium_strings.grd"
BLOB = "dea90cda0be32f0d6c94ce7950a00970aded6299"
KEYS = ("IDS_PRODUCT_NAME", "IDS_SHORT_PRODUCT_NAME")


def rewrite(raw):
    source = raw.decode("utf-8", errors="strict")
    ET.fromstring(source)
    result = source
    for key in KEYS:
        # Only plain non-translatable Chromium names; never Chrome for Testing.
        pattern = (r'(<message\b(?=[^>]*\bname="' + re.escape(key)
                   + r'")(?=[^>]*\btranslateable="false")[^>]*>[ \t\r\n]*)'
                   + r'Chromium([ \t\r\n]*</message>)')
        result, count = re.subn(pattern, lambda m: m[1] + "VEIL" + m[2], result)
        if count != 1:
            raise ValueError("Expected exactly one plain Chromium message: " + key)
    ET.fromstring(result)
    return result.encode("utf-8")


def patch_bytes(original, modified):
    diff = "".join(difflib.unified_diff(original.decode("utf-8").splitlines(keepends=True),
                                       modified.decode("utf-8").splitlines(keepends=True),
                                       fromfile="a/" + REL, tofile="b/" + REL))
    if not diff:
        raise ValueError("Empty diff")
    return ("diff --git a/" + REL + " b/" + REL + "\n" + diff).encode("utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--verify-registered", action="store_true")
    options = parser.parse_args()
    pin = json.loads((ROOT / "chromium/candidate.json").read_text(encoding="utf-8"))
    if pin["revision"] != REV:
        raise ValueError("Review candidate generator for updated upstream revision")
    url = "https://chromium.googlesource.com/chromium/src/+/" + REV + "/" + REL + "?format=TEXT"
    with urllib.request.urlopen(url, timeout=45) as response:
        encoded = response.read(8 * 1024 * 1024 + 1)
    if len(encoded) > 8 * 1024 * 1024:
        raise ValueError("Unexpected upstream response size")
    original = base64.b64decode(encoded.strip(), validate=True)
    blob = hashlib.sha1(b"blob " + str(len(original)).encode() + b"\0" + original).hexdigest()
    if blob != BLOB:
        raise ValueError("Pinned GRIT blob mismatch")
    modified = rewrite(original)
    patch = patch_bytes(original, modified)
    with tempfile.TemporaryDirectory(prefix="veil-ui-candidate-") as tmp:
        root = pathlib.Path(tmp)
        target = root / REL
        target.parent.mkdir(parents=True)
        target.write_bytes(original)
        subprocess.run(["git", "-C", tmp, "init", "-q"], check=True, timeout=20)
        for check in (["--check"], []):
            subprocess.run(["git", "-C", tmp, "apply", *check, "--whitespace=error-all", "-"],
                           input=patch, check=True, timeout=20)
        if target.read_bytes() != modified:
            raise ValueError("Patch result mismatch")
    if options.verify_registered:
        series = json.loads((ROOT / "chromium/patches/series.json").read_text(encoding="utf-8"))
        name = "0002-veil-ui-product-name.patch"
        entries = [p for p in series["patches"] if p["file"] == name]
        if (series["baseRevision"] != REV or len(entries) != 1
                or entries[0]["sha256"] != hashlib.sha256(patch).hexdigest()
                or (ROOT / "chromium/patches" / name).read_bytes() != patch):
            raise ValueError("Registered patch differs from regenerated source patch")
    print(json.dumps({"status": "REGISTERED_UI_FIXTURE_CHECKED_ONLY" if options.verify_registered else "REVIEW_CANDIDATE_ONLY", "revision": REV, "blob": blob,
                      "patchSha256": hashlib.sha256(patch).hexdigest(), "keys": KEYS,
                      "registered": options.verify_registered, "gritCompile": "NOT RUN", "runtime": "NOT RUN",
                      "releaseApproved": False}))
    print("BEGIN REVIEW DIFF")
    print(patch.decode("utf-8"), end="")
    print("END REVIEW DIFF")

if __name__ == "__main__":
    main()
