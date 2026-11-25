# SPDX-License-Identifier: MPL-2.0
"""Read exact local Git objects, never evaluate upstream source or fetch dependencies."""
import argparse
import hashlib
import json
import os
import pathlib
import re
import subprocess

from observe_build_requirements import HEX, ROOT, observe


class LocalObjects:
    def __init__(self, checkout, revision):
        if not HEX.fullmatch(revision):
            raise ValueError("Expected exact commit identity")
        self.checkout = pathlib.Path(checkout).resolve(strict=True)
        if not self.checkout.is_dir():
            raise ValueError("Expected local Git directory")
        self.env = {k: v for k, v in os.environ.items() if not k.upper().startswith("GIT_")}
        self.env.update(GIT_NO_REPLACE_OBJECTS="1", GIT_NO_LAZY_FETCH="1",
                        GIT_ALLOW_PROTOCOL="", GIT_TERMINAL_PROMPT="0",
                        GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL=os.devnull)
        if self.git("rev-parse", "--show-object-format").strip() != b"sha1":
            raise ValueError("Expected SHA-1 Git object format")
        self.revision = revision
        raw = self.object(revision, "commit", 1024 * 1024)
        match = re.fullmatch(rb"tree ([0-9a-f]{40})", raw.split(b"\n", 1)[0])
        if not match:
            raise ValueError("Invalid commit tree header")
        self.root = match[1].decode("ascii")

    def git(self, *args):
        return subprocess.run(["git", "-C", str(self.checkout), *args], env=self.env,
                              check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                              timeout=20).stdout

    def object(self, oid, kind, limit):
        if not HEX.fullmatch(oid):
            raise ValueError("Invalid object ID")
        if self.git("cat-file", "-t", oid).strip() != kind.encode("ascii"):
            raise ValueError("Unexpected Git object type")
        size = int(self.git("cat-file", "-s", oid))
        if not 0 <= size <= limit:
            raise ValueError("Git object exceeds size limit")
        raw = self.git("cat-file", kind, oid)
        header = (kind + " " + str(len(raw))).encode("ascii") + b"\0"
        if len(raw) != size or hashlib.sha1(header + raw).hexdigest() != oid:
            raise ValueError("Git object hash mismatch")
        return raw

    def tree(self, oid):
        raw = self.object(oid, "tree", 2 * 1024 * 1024)
        entries = {}
        modes = {b"40000": "tree", b"100644": "blob", b"100755": "blob",
                 b"120000": "blob", b"160000": "commit"}
        offset = 0
        while offset < len(raw):
            end = raw.index(b"\0", offset)
            mode, name = raw[offset:end].split(b" ", 1)
            name = name.decode("utf-8", errors="strict")
            if (mode not in modes or not name or name in (".", "..") or
                    "/" in name or name in entries or end + 21 > len(raw)):
                raise ValueError("Malformed Git tree entry")
            entries[name] = {"id": raw[end + 1:end + 21].hex(),
                             "mode": int(mode, 8), "type": modes[mode]}
            offset = end + 21
        return entries

    def read_tree(self, revision, directory):
        if revision != self.revision or directory not in ("", "docs", "build"):
            raise ValueError("Unexpected local tree request")
        oid = self.root
        if directory:
            entry = self.tree(oid)[directory]
            if entry["type"] != "tree":
                raise ValueError("Expected directory tree")
            oid = entry["id"]
        return oid, self.tree(oid)

    def read_blob(self, revision, path, expected, limit):
        if revision != self.revision:
            raise ValueError("Unexpected revision")
        return self.object(expected, "blob", limit)


def observe_local(checkout, revision):
    objects = LocalObjects(checkout, revision)
    report = observe(revision, tree_reader=objects.read_tree, blob_reader=objects.read_blob)
    report["source"] = "LOCAL_GIT_OBJECTS_ONLY"
    report["commitObjectHashVerified"] = True
    report["remoteOriginVerified"] = False
    return report


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("checkout", help="Existing trusted local Git checkout or bare repository")
    args = parser.parse_args()
    pin = json.loads((ROOT / "chromium/candidate.json").read_text(encoding="utf-8"))
    print(json.dumps(observe_local(args.checkout, pin["revision"]), indent=2, ensure_ascii=True))
