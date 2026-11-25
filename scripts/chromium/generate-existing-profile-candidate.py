# SPDX-License-Identifier: MPL-2.0
"""Generate an unregistered, source-only early startup experiment."""
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
ANCHOR = '''  std::wstring user_data_dir =
      GetCommandLineSwitchValue(command_line, kUserDataDirSwitch);

  GetUserDataDirFromRegistryPolicyIfSet(mode, &user_data_dir);
'''
INSERT = '''  // VEIL development experiment only; not an authorization boundary.
  // This opt-in branch never creates a directory or requests a fallback.
  if (GetCommandLineSwitch(command_line,
                           L"veil-dev-existing-profile-only").has_value()) {
    const std::wstring explicit_path = user_data_dir;
    GetUserDataDirFromRegistryPolicyIfSet(mode, &user_data_dir);
    if (explicit_path.empty() || explicit_path != user_data_dir ||
        IsHeadlessMode(command_line) ||
        MakeAbsoluteFilePath(user_data_dir) != user_data_dir) {
      return false;
    }
    const DWORD attributes = ::GetFileAttributesW(user_data_dir.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES ||
        !(attributes & FILE_ATTRIBUTE_DIRECTORY) ||
        (attributes & FILE_ATTRIBUTE_REPARSE_POINT)) {
      return false;
    }
    *result = user_data_dir;
    return true;
  }

'''
REPLACEMENT = ANCHOR.replace(
    "  GetUserDataDirFromRegistryPolicyIfSet(mode, &user_data_dir);\n",
    INSERT + "  GetUserDataDirFromRegistryPolicyIfSet(mode, &user_data_dir);\n",
)


def rewrite(raw):
    source = raw.decode("utf-8", errors="strict")
    if source.count(ANCHOR) != 1 or "veil-dev-existing-profile-only" in source:
        raise ValueError("Expected one exact unmodified startup anchor")
    return source.replace(ANCHOR, REPLACEMENT, 1).encode("utf-8")


def create_patch(original, modified):
    diff = "".join(difflib.unified_diff(
        original.decode().splitlines(keepends=True),
        modified.decode().splitlines(keepends=True),
        fromfile="a/" + REL, tofile="b/" + REL))
    return ("diff --git a/" + REL + " b/" + REL + "\n" + diff).encode()


def main():
    pin = json.loads((ROOT / "chromium/candidate.json").read_text(encoding="utf-8"))
    if pin["revision"] != REV:
        raise ValueError("Review startup experiment for the new revision")
    original = fetch_pinned_blob(REV, REL, BLOB, 8 * 1024 * 1024)
    modified = rewrite(original)
    patch = create_patch(original, modified)
    with tempfile.TemporaryDirectory(prefix="veil-startup-candidate-") as tmp:
        target = pathlib.Path(tmp) / REL
        target.parent.mkdir(parents=True)
        target.write_bytes(original)
        subprocess.run(["git", "-C", tmp, "init", "-q"], check=True, timeout=20)
        for options in (["--check"], []):
            subprocess.run(["git", "-C", tmp, "apply", *options,
                            "--whitespace=error-all", "-"],
                           input=patch, check=True, timeout=20)
        if target.read_bytes() != modified:
            raise ValueError("Patch result mismatch")
    print(json.dumps({"status": "EARLY_STARTUP_SOURCE_CANDIDATE_ONLY",
                      "revision": REV, "blob": BLOB,
                      "patchSha256": hashlib.sha256(patch).hexdigest(),
                      "registered": False, "compiled": "NOT RUN",
                      "profileIsolation": "NOT RUN", "runtime": "NOT RUN",
                      "releaseApproved": False}))
    print("BEGIN REVIEW DIFF")
    print(patch.decode(), end="")
    print("END REVIEW DIFF")


if __name__ == "__main__":
    main()
