# SPDX-License-Identifier: MPL-2.0
"""Compose unregistered early startup experiments against two pinned source blobs."""
import difflib
import hashlib
import importlib.util
import json
import pathlib
import subprocess
import tempfile
from pinned_gitiles import fetch_pinned_blob

ROOT = pathlib.Path(__file__).resolve().parents[2]
REV = "1de57fa19722fa6d6841d4f596c394d015580656"
DELEGATE = "chrome/app/chrome_main_delegate.cc"
DELEGATE_BLOB = "33a3b3844ad4839eb87c15c7aef0c956abd9f98e"
ORIGINAL = '''  wchar_t user_data_dir_buf[MAX_PATH], invalid_user_data_dir_buf[MAX_PATH];

  // In tests this may return false, implying the user data dir should be unset.
  if (GetUserDataDirectoryThunk(user_data_dir_buf, std::size(user_data_dir_buf),
                                invalid_user_data_dir_buf,
                                std::size(invalid_user_data_dir_buf))) {
'''
MODIFIED = '''  wchar_t user_data_dir_buf[MAX_PATH] = {};
  wchar_t invalid_user_data_dir_buf[MAX_PATH] = {};

  const bool got_user_data_dir =
      GetUserDataDirectoryThunk(user_data_dir_buf, std::size(user_data_dir_buf),
                                invalid_user_data_dir_buf,
                                std::size(invalid_user_data_dir_buf));
  // Development-only experiment. CHECK is not an approved production failure
  // mechanism; crash handling and earlier process initialization remain unverified.
  if (command_line->HasSwitch("veil-dev-existing-profile-only")) {
    CHECK(got_user_data_dir);
    CHECK(user_data_dir_buf[0] != 0);
    CHECK(invalid_user_data_dir_buf[0] == 0);
    CHECK(base::PathService::OverrideAndCreateIfNeeded(
        chrome::DIR_USER_DATA, base::FilePath(user_data_dir_buf), false, false));
    return;
  }

  // In tests this may return false, implying the user data dir should be unset.
  if (got_user_data_dir) {
'''


def load_candidate(filename):
    spec = importlib.util.spec_from_file_location(filename, pathlib.Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def rewrite_delegate(raw):
    source = raw.decode("utf-8", errors="strict")
    if source.count(ORIGINAL) != 1 or "veil-dev-existing-profile-only" in source:
        raise ValueError("Expected exactly one unmodified Windows delegate anchor")
    return source.replace(ORIGINAL, MODIFIED, 1).encode("utf-8")


def compose_early(raw, early, cache):
    # Independent rewrites must commute; refuse overlapping changes.
    first = cache.rewrite(early.rewrite(raw))
    second = early.rewrite(cache.rewrite(raw))
    if first != second:
        raise ValueError("Early and cache candidate composition differs by order")
    return first


def patch_for(path, original, modified):
    return ("diff --git a/" + path + " b/" + path + "\n" + "".join(
        difflib.unified_diff(original.decode().splitlines(keepends=True),
                             modified.decode().splitlines(keepends=True),
                             fromfile="a/" + path, tofile="b/" + path))).encode()


def main():
    pin = json.loads((ROOT / "chromium/candidate.json").read_text(encoding="utf-8"))
    early = load_candidate("generate-existing-profile-candidate.py")
    cache = load_candidate("generate-cache-failure-candidate.py")
    if pin["revision"] != REV or early.REV != REV or cache.REV != REV:
        raise ValueError("Re-review all candidates for new revision")
    if (early.REL, early.BLOB) != (cache.REL, cache.BLOB):
        raise ValueError("Candidate source identities differ")
    original = fetch_pinned_blob(REV, early.REL, early.BLOB, 8 * 1024 * 1024)
    delegate = fetch_pinned_blob(REV, DELEGATE, DELEGATE_BLOB, 8 * 1024 * 1024)
    rows = [(early.REL, original, compose_early(original, early, cache)),
            (DELEGATE, delegate, rewrite_delegate(delegate))]
    patch = b"".join(patch_for(*row) for row in rows)
    with tempfile.TemporaryDirectory(prefix="veil-delegate-candidate-") as tmp:
        for path, before, _ in rows:
            target = pathlib.Path(tmp) / path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(before)
        subprocess.run(["git", "-C", tmp, "init", "-q"], check=True, timeout=20)
        for options in (["--check"], []):
            subprocess.run(["git", "-C", tmp, "apply", *options,
                            "--whitespace=error-all", "-"],
                           input=patch, check=True, timeout=20)
        for path, _, after in rows:
            if (pathlib.Path(tmp) / path).read_bytes() != after:
                raise ValueError("Combined patch result mismatch")
    print(json.dumps({"status": "COMBINED_STARTUP_SOURCE_CANDIDATE_ONLY",
                      "revision": REV, "sourceBlobs": [early.BLOB, DELEGATE_BLOB],
                      "patchSha256": hashlib.sha256(patch).hexdigest(),
                      "registered": False, "chromiumBuild": "NOT RUN",
                      "crashReporting": "NOT RUN", "childStartup": "NOT RUN",
                      "runtime": "NOT RUN", "releaseApproved": False}))
    print("BEGIN REVIEW DIFF")
    print(patch.decode(), end="")
    print("END REVIEW DIFF")


if __name__ == "__main__":
    main()
