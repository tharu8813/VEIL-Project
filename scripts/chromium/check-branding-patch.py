# SPDX-License-Identifier: MPL-2.0
"""Verify one patch against the pinned upstream file, not the whole source tree."""
import hashlib
import json
import pathlib
import subprocess
import tempfile
from pinned_gitiles import fetch_pinned_blob

ROOT = pathlib.Path(__file__).resolve().parents[2]
REL = pathlib.Path("chrome/app/theme/chromium/BRANDING")
EXPECTED_REVISION = "1de57fa19722fa6d6841d4f596c394d015580656"
EXPECTED_BLOB = "f8363d5b294fe210ebfdd18374e95b54d6ff5c17"
PATCH_NAME = "0001-veil-product-name.patch"


def main():
    pin = json.loads((ROOT / "chromium/candidate.json").read_text(encoding="utf-8"))
    series = json.loads((ROOT / "chromium/patches/series.json").read_text(encoding="utf-8"))
    if pin["revision"] != EXPECTED_REVISION or series["baseRevision"] != EXPECTED_REVISION:
        raise ValueError("Branding evidence must be reviewed for the new upstream revision")
    entries = [p for p in series["patches"] if p["file"] == PATCH_NAME]
    if len(entries) != 1 or series["patches"][0]["file"] != PATCH_NAME:
        raise ValueError("Expected first product name patch")
    patch = (ROOT / "chromium/patches" / PATCH_NAME).read_bytes()
    if hashlib.sha256(patch).hexdigest() != entries[0]["sha256"]:
        raise ValueError("Patch hash mismatch")
    original = fetch_pinned_blob(EXPECTED_REVISION, REL.as_posix(), EXPECTED_BLOB, 16384)
    blob = EXPECTED_BLOB
    expected = original
    for key in (b"PRODUCT_FULLNAME", b"PRODUCT_SHORTNAME"):
        old = key + b"=Chromium\n"
        if expected.splitlines(keepends=True).count(old) != 1:
            raise ValueError("Expected unique product key")
        expected = expected.replace(old, key + b"=VEIL\n")
    with tempfile.TemporaryDirectory(prefix="veil-branding-") as directory:
        temp = pathlib.Path(directory)
        target = temp / REL
        target.parent.mkdir(parents=True)
        target.write_bytes(original)
        def git(*args, **kwargs):
            return subprocess.run(["git", "-C", directory, *args], check=True, timeout=30, **kwargs)
        git("init", "-q")
        git("apply", "--check", "--whitespace=error-all", "-", input=patch)
        git("apply", "--whitespace=error-all", "-", input=patch)
        if target.read_bytes() != expected:
            raise ValueError("Unexpected branding changes")
        files = sorted(p.relative_to(temp).as_posix() for p in temp.rglob("*")
                       if p.is_file() and ".git" not in p.relative_to(temp).parts)
        if files != [REL.as_posix()]:
            raise ValueError("Unexpected files created")
    print(json.dumps({"status": "PINNED_BRANDING_FIXTURE_CHECKED_ONLY",
                      "revision": EXPECTED_REVISION, "blob": blob,
                      "changedKeys": ["PRODUCT_FULLNAME", "PRODUCT_SHORTNAME"],
                      "fullSeries": "NOT RUN", "build": "NOT RUN", "runtime": "NOT RUN",
                      "sandbox": "NOT RUN", "releaseApproved": False}))


if __name__ == "__main__":
    main()
