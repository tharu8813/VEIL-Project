# SPDX-License-Identifier: MPL-2.0
import json
import pathlib
import sys
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2] / "scripts/chromium"))
from observe_build_requirements import observe, read_tree


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
        body = b")]}'\n" + json.dumps({"id": "a" * 40, "entries": [entry]}).encode()
        self.assertEqual(read_tree("a" * 40, "", opener=lambda url, timeout: Response(url, body))[1]["DEPS"], entry)

    def test_observation_binds_tree_and_blob(self):
        def entry(kind, digest):
            return {"type": kind, "id": digest * 40, "mode": 33188}
        trees = {
            "": ("a"*40, {"docs": entry("tree","b"), "build": entry("tree","c"), "DEPS": entry("blob","d")}),
            "docs": ("b"*40, {"windows_build_instructions.md": entry("blob","e")}),
            "build": ("c"*40, {"vs_toolchain.py": entry("blob","f")}),
        }
        calls = []
        def blob(rev, path, digest, limit):
            calls.append((path, digest))
            return b"Windows SDK fixture\n"
        result = observe("1"*40, tree_reader=lambda rev, path: trees[path], blob_reader=blob)
        self.assertEqual(len(calls), 3)
        self.assertEqual(calls[0][1], "e"*40)
        self.assertEqual(result["status"], "PINNED_SOURCE_OBSERVED_ONLY")
        self.assertFalse(result["requirementsReviewed"])
        self.assertEqual(result["build"], "NOT RUN")
        trees["docs"] = ("c"*40, trees["docs"][1])
        with self.assertRaises(ValueError):
            observe("1"*40, tree_reader=lambda rev, path: trees[path], blob_reader=blob)
        self.assertEqual(len(calls), 3)


if __name__ == "__main__":
    unittest.main()
