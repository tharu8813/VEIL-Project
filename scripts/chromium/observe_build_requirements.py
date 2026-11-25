# SPDX-License-Identifier: MPL-2.0
"""Read-only source observation; never evaluate DEPS or execute upstream code."""
import hashlib
import json
import pathlib
import re
import time
import urllib.error
import urllib.request

from pinned_gitiles import BASE, fetch_pinned_blob, MAX_ATTEMPTS, TRANSIENT_HTTP

ROOT = pathlib.Path(__file__).resolve().parents[2]
PATHS = ("docs/windows_build_instructions.md", "build/vs_toolchain.py", "DEPS")
HEX = re.compile(r"[0-9a-f]{40}")
PATTERNS = re.compile(
    r"visual studio|windows sdk|[0-9]{2,} ?(?:GB|GiB)|"
    r"vs_version|sdk_version|win_sdk|msvc|pre_deps_hooks|"
    r"DEPOT_TOOLS_WIN_TOOLCHAIN|custom_vars|target_os|windows_sdk",
    re.IGNORECASE,
)


def verify_tree_identity(tree_id, entries):
    """Reconstruct Git's canonical tree bytes, not just the reported ID."""
    modes = {0o40000: "tree", 0o100644: "blob", 0o100755: "blob",
             0o120000: "blob", 0o160000: "commit"}
    records = []
    for name, item in entries.items():
        mode = item.get("mode")
        if type(mode) is not int or modes.get(mode) != item.get("type") or mode not in modes:
            raise ValueError("Invalid Git tree mode/type")
        if name in (".", "..") or "\0" in name or "/" in name:
            raise ValueError("Invalid Git tree filename")
        encoded = name.encode("utf-8", errors="strict")
        sort_key = encoded + (b"/" if mode == 0o40000 else b"")
        record = format(mode, "o").encode("ascii") + b" " + encoded + b"\0" + bytes.fromhex(item["id"])
        records.append((sort_key, record))
    raw = b"".join(record for _, record in sorted(records))
    actual = hashlib.sha1(b"tree " + str(len(raw)).encode("ascii") + b"\0" + raw).hexdigest()
    if actual != tree_id:
        raise ValueError("Git tree content hash mismatch")


def read_tree(revision, directory, *, opener=None, sleeper=None):
    if not HEX.fullmatch(revision) or directory not in ("", "docs", "build"):
        raise ValueError("Unexpected revision or tree path")
    url = BASE + revision + "/" + directory + "?format=JSON"
    opener = opener or urllib.request.urlopen
    sleeper = sleeper or time.sleep
    limit = 2 * 1024 * 1024
    for attempt in range(1, MAX_ATTEMPTS + 1):
        try:
            with opener(url, timeout=20) as response:
                if response.geturl() != url:
                    raise ValueError("Unexpected tree response URL")
                data = response.read(limit + 1)
        except urllib.error.HTTPError as error:
            error.close()
            if error.code not in TRANSIENT_HTTP or attempt == MAX_ATTEMPTS:
                raise
        except (TimeoutError, ConnectionError):
            if attempt == MAX_ATTEMPTS:
                raise
        except urllib.error.URLError as error:
            if not isinstance(error.reason, (TimeoutError, ConnectionError)) or attempt == MAX_ATTEMPTS:
                raise
        else:
            if len(data) > limit or not data.startswith(b")]}'\n"):
                raise ValueError("Unexpected Gitiles tree response")
            result = json.loads(data[5:])
            if not HEX.fullmatch(result.get("id", "")):
                raise ValueError("Missing tree identity")
            entries = {}
            for item in result["entries"]:
                name = item["name"]
                if not isinstance(name, str) or not name or name in entries or "/" in name:
                    raise ValueError("Invalid or duplicate entry")
                if not HEX.fullmatch(item.get("id", "")):
                    raise ValueError("Invalid entry identity")
                entries[name] = item
            verify_tree_identity(result["id"], entries)
            return result["id"], entries
        sleeper(2 ** (attempt - 1))
    raise AssertionError("Unreachable retry state")


def observe(revision, *, tree_reader=read_tree, blob_reader=fetch_pinned_blob):
    root_id, root = tree_reader(revision, "")
    trees = {"": root}
    for directory in ("docs", "build"):
        parent = root[directory]
        observed_id, entries = tree_reader(revision, directory)
        if parent["type"] != "tree" or parent["id"] != observed_id:
            raise ValueError("Child tree identity mismatch")
        trees[directory] = entries
    files = []
    for path in PATHS:
        directory, _, name = path.rpartition("/")
        entry = trees[directory][name]
        if entry["type"] != "blob" or entry["mode"] not in (33188, 33261):
            raise ValueError("Non-ordinary source entry")
        raw = blob_reader(revision, path, entry["id"], 8 * 1024 * 1024)
        lines = raw.decode("utf-8", errors="strict").splitlines()
        matches = [{"line": i, "text": line[:300]} for i, line in enumerate(lines, 1)
                   if PATTERNS.search(line)]
        files.append({"path": path, "gitBlob": entry["id"],
                      "sha256": hashlib.sha256(raw).hexdigest(), "bytes": len(raw),
                      "matchedLines": len(matches), "sample": matches[:100]})
    return {"status": "PINNED_SOURCE_OBSERVED_ONLY", "revision": revision,
            "rootTree": root_id, "files": files, "requirementsReviewed": False,
            "dependencyGraph": "NOT VERIFIED", "toolchainCompatibility": "NOT RUN",
            "build": "NOT RUN", "sandbox": "NOT RUN"}


if __name__ == "__main__":
    pin = json.loads((ROOT / "chromium/candidate.json").read_text(encoding="utf-8"))
    revision = pin["revision"]
    if not HEX.fullmatch(revision):
        raise ValueError("Invalid candidate revision")
    print(json.dumps(observe(revision), indent=2, ensure_ascii=True))
