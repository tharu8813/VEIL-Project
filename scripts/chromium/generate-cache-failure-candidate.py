# SPDX-License-Identifier: MPL-2.0
"""Review-only cache failure candidate. No registered patch or upstream execution."""
import difflib
import hashlib
import json
import pathlib
import subprocess
import tempfile
from pinned_gitiles import fetch_pinned_blob

ROOT = pathlib.Path(__file__).resolve().parents[2]
REV = "1de57fa19722fa6d6841d4f596c394d015580656"
REL = "chrome/install_static/user_data_dir.cc"
BLOB = "6b231841bf68c1abd8087bb1c0ea70f3ef3c9ece"
ORIGINAL = '''bool GetUserDataDirectory(std::wstring* user_data_dir,
                          std::wstring* invalid_user_data_dir) {
  if (!g_user_data_dir) {
    g_user_data_dir = new std::wstring();
    g_invalid_user_data_dir = new std::wstring();
    if (!GetUserDataDirectoryUsingProcessCommandLine(
            InstallDetails::Get().mode(), g_user_data_dir,
            g_invalid_user_data_dir)) {
      return false;
    }
    assert(!g_user_data_dir->empty());
  }
  *user_data_dir = *g_user_data_dir;
  if (invalid_user_data_dir)
    *invalid_user_data_dir = *g_invalid_user_data_dir;
  return true;
}
'''
MODIFIED = ORIGINAL.replace(
    "      return false;\n",
    "      // Keep a failed initialization failed on subsequent queries.\n"
    "      g_user_data_dir->clear();\n"
    "      g_invalid_user_data_dir->clear();\n"
    "      return false;\n",
    1,
).replace(
    "    assert(!g_user_data_dir->empty());\n  }\n",
    "  }\n"
    "  if (g_user_data_dir->empty())\n"
    "    return false;\n",
    1,
)


def rewrite(raw):
    source = raw.decode("utf-8", errors="strict")
    if source.count(ORIGINAL) != 1 or MODIFIED in source:
        raise ValueError("Expected exactly one original cache function")
    return source.replace(ORIGINAL, MODIFIED, 1).encode("utf-8")


def main():
    pin = json.loads((ROOT / "chromium/candidate.json").read_text(encoding="utf-8"))
    if pin["revision"] != REV:
        raise ValueError("Re-review candidate for new upstream revision")
    original = fetch_pinned_blob(REV, REL, BLOB, 8 * 1024 * 1024)
    modified = rewrite(original)
    diff = "".join(difflib.unified_diff(
        original.decode().splitlines(keepends=True),
        modified.decode().splitlines(keepends=True),
        fromfile="a/" + REL, tofile="b/" + REL))
    patch = ("diff --git a/" + REL + " b/" + REL + "\n" + diff).encode()
    with tempfile.TemporaryDirectory(prefix="veil-cache-candidate-") as tmp:
        path = pathlib.Path(tmp) / REL
        path.parent.mkdir(parents=True)
        path.write_bytes(original)
        subprocess.run(["git", "-C", tmp, "init", "-q"], check=True, timeout=20)
        for options in (["--check"], []):
            subprocess.run(["git", "-C", tmp, "apply", *options,
                            "--whitespace=error-all", "-"],
                           input=patch, check=True, timeout=20)
        if path.read_bytes() != modified:
            raise ValueError("Patch result mismatch")
    print(json.dumps({"status": "CACHE_FAILURE_SOURCE_CANDIDATE_ONLY",
                      "revision": REV, "blob": BLOB,
                      "patchSha256": hashlib.sha256(patch).hexdigest(),
                      "registered": False, "chromiumBuild": "NOT RUN",
                      "runtime": "NOT RUN", "releaseApproved": False}))
    print("BEGIN REVIEW DIFF")
    print(patch.decode(), end="")
    print("END REVIEW DIFF")


if __name__ == "__main__":
    main()
