# SPDX-License-Identifier: MPL-2.0
import json
import pathlib
import sys
import subprocess
import tempfile
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2] / "scripts/chromium"))
from observe_build_requirements import observe, read_tree, verify_tree_identity


class Response:
    def __init__(self, url, data):
        self.url, self.data = url, data
    def __enter__(self):
        return self
    def __exit__(self, *args):
        pass
    def geturl(self):
        return self.url
    def read(self, limit):
        return self.data[:limit]


class RequirementsTest(unittest.TestCase):
    def test_tree_rejects_redirect_duplicate_and_bad_prefix(self):
        entry = {"name": "DEPS", "type": "blob", "mode": 33188, "id": "b" * 40}
        for body in (b"{}", b")]}'\n" + json.dumps(
                {"id": "a" * 40, "entries": [entry, entry]}).encode()):
            with self.assertRaises(ValueError):
                read_tree("a" * 40, "", opener=lambda url, timeout: Response(url, body))
        with self.assertRaises(ValueError):
            read_tree("a" * 40, "", opener=lambda url, timeout: Response(url + "x", b""))
        tree_id = self.git_tree_id([entry])
        body = b")]}'\n" + json.dumps({"id": tree_id, "entries": [entry]}).encode()
        self.assertEqual(read_tree("a" * 40, "", opener=lambda url, timeout: Response(url, body))[1]["DEPS"], entry)

    @staticmethod
    def git_tree_id(entries):
        # Independent Git oracle: mktree handles canonical directory ordering.
        with tempfile.TemporaryDirectory() as root:
            subprocess.run(["git", "init", "--bare", "-q", root], check=True)
            raw = b"".join((format(e["mode"], "o") + " " + e["type"] + " " +
                            e["id"] + "\t" + e["name"]).encode("utf-8") + b"\0"
                           for e in entries)
            return subprocess.check_output(["git", "-C", root, "mktree", "--missing", "-z"],
                                           input=raw).decode("ascii").strip()

    def test_git_tree_content_hash_and_tamper(self):
        entries = [
            {"name": "dir", "type": "tree", "mode": 16384, "id": "a" * 40},
            {"name": "dir.txt", "type": "blob", "mode": 33188, "id": "b" * 40},
            {"name": "é.txt", "type": "blob", "mode": 33261, "id": "c" * 40},
            {"name": "link", "type": "blob", "mode": 40960, "id": "d" * 40},
            {"name": "module", "type": "commit", "mode": 57344, "id": "e" * 40},
        ]
        digest = self.git_tree_id(entries)
        verify_tree_identity(digest, {e["name"]: e for e in entries})
        body = b")]}'\n" + json.dumps({"id": digest, "entries": entries}).encode()
        self.assertEqual(read_tree("a" * 40, "", opener=lambda url, timeout: Response(url, body))[0], digest)
        for field, value in (("id", "f" * 40), ("mode", 33261), ("type", "commit")):
            changed = [dict(e) for e in entries]
            changed[1][field] = value
            body = b")]}'\n" + json.dumps({"id": digest, "entries": changed}).encode()
            with self.assertRaises(ValueError):
                read_tree("a" * 40, "", opener=lambda url, timeout: Response(url, body))

    def test_observation_binds_tree_and_blob(self):
        def entry(kind, digest):
            return {"type": kind, "id": digest * 40, "mode": 16384 if kind == "tree" else 33188}
        trees = {
            "": ("a"*40, {"docs": entry("tree","b"), "build": entry("tree","c"), "DEPS": entry("blob","d")}),
            "docs": ("b"*40, {"windows_build_instructions.md": entry("blob","e")}),
            "build": ("c"*40, {"vs_toolchain.py": entry("blob","f"), "toolchain": entry("tree","7")}),
            "build/toolchain": ("7"*40, {"win": entry("tree","8")}),
            "build/toolchain/win": ("8"*40, {"setup_toolchain.py": entry("blob","9")}),
        }
        calls = []
        def blob(rev, path, digest, limit):
            calls.append((path, digest))
            return b"Windows SDK fixture\n"
        result = observe("1"*40, tree_reader=lambda rev, path: trees[path], blob_reader=blob)
        self.assertEqual(len(calls), 4)
        self.assertEqual(calls[0][1], "e"*40)
        self.assertEqual(result["status"], "PINNED_SOURCE_OBSERVED_ONLY")
        self.assertFalse(result["requirementsReviewed"])
        self.assertEqual(result["build"], "NOT RUN")
        trees["docs"] = ("c"*40, trees["docs"][1])
        with self.assertRaises(ValueError):
            observe("1"*40, tree_reader=lambda rev, path: trees[path], blob_reader=blob)
        self.assertEqual(len(calls), 4)


if __name__ == "__main__":
    unittest.main()
