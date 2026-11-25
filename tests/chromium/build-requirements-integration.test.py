# SPDX-License-Identifier: MPL-2.0
"""Offline integration: actual Git objects served through synthetic Gitiles responses."""
import base64
import functools
import hashlib
import io
import json
import pathlib
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2] / "scripts/chromium"))
from observe_build_requirements import observe, read_tree, PATHS
from observe_local_build_requirements import observe_local
from pinned_gitiles import BASE, fetch_pinned_blob


class Response(io.BytesIO):
    def __init__(self, url, data):
        super().__init__(data)
        self.url = url

    def geturl(self):
        return self.url


class BuildRequirementsIntegration(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.root = pathlib.Path(temp.name)
        self.git("init", "--object-format=sha1", "-q")
        self.sources = {path: ("Windows SDK synthetic fixture: " + path + "\n").encode()
                        for path in PATHS}
        self.sources["build/vs_toolchain.py"] += b"raise RuntimeError('must never execute')\n"
        for path, data in self.sources.items():
            file = self.root / path
            file.parent.mkdir(parents=True, exist_ok=True)
            file.write_bytes(data)
        (self.root / "docs-extra").write_bytes(b"prefix sort fixture")
        self.git("add", "--all")
        self.git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
                 "commit", "-qm", "fixture")
        self.revision = self.git("rev-parse", "HEAD").decode().strip()

    def git(self, *args):
        return subprocess.check_output(["git", "-C", str(self.root), *args])

    def tree_json(self, path):
        spec = self.revision + ":" + path if path else self.revision + "^{tree}"
        oid = self.git("rev-parse", spec).decode().strip()
        entries = []
        for raw in self.git("ls-tree", "-z", oid).split(b"\0"):
            if not raw:
                continue
            fields, name = raw.split(b"\t", 1)
            mode, kind, entry_id = fields.decode().split()
            entries.append({"mode": int(mode, 8), "type": kind,
                            "id": entry_id, "name": name.decode()})
        return {"id": oid, "entries": entries}

    def simulate(self, tamper=None):
        calls = []
        prefix = BASE + self.revision + "/"

        def opener(url, timeout):
            self.assertTrue(url.startswith(prefix))
            path, fmt = url[len(prefix):].split("?format=")
            calls.append((path, fmt))
            if fmt == "JSON":
                tree = self.tree_json(path)
                if tamper == "tree" and path == "docs":
                    tree["entries"][0]["id"] = "a" * 40
                if tamper == "child" and path == "build":
                    tree = {"id": hashlib.sha1(b"tree 0\0").hexdigest(), "entries": []}
                data = b")]}'\n" + json.dumps(tree).encode()
            elif fmt == "TEXT":
                data = base64.b64encode(self.git("show", self.revision + ":" + path))
                if tamper == "blob" and path == "DEPS":
                    data = base64.b64encode(b"modified")
                if tamper == "redirect" and path == "DEPS":
                    return Response(url + "-redirected", data)
            else:
                self.fail("Unexpected response format")
            return Response(url, data)

        report = observe(self.revision, tree_reader=functools.partial(read_tree, opener=opener),
                         blob_reader=functools.partial(fetch_pinned_blob, opener=opener))
        return report, calls

    def test_remote_and_local_observations_agree(self):
        remote, calls = self.simulate()
        local = observe_local(self.root, self.revision)
        self.assertEqual(calls, [("", "JSON"), ("docs", "JSON"), ("build", "JSON")] +
                         [(p, "TEXT") for p in PATHS])
        for key in remote:
            self.assertEqual(remote[key], local[key], key)
        self.assertEqual({f["path"]: f["sha256"] for f in remote["files"]},
                         {p: hashlib.sha256(b).hexdigest() for p, b in self.sources.items()})
        self.assertFalse(remote["requirementsReviewed"])
        self.assertEqual(remote["build"], "NOT RUN")
        self.assertTrue(local["commitObjectHashVerified"])
        self.assertFalse(local["remoteOriginVerified"])

    def test_modified_tree_refused(self):
        with self.assertRaisesRegex(ValueError, "Git tree content hash mismatch"):
            self.simulate("tree")

    def test_valid_but_wrong_child_tree_refused(self):
        with self.assertRaisesRegex(ValueError, "Child tree identity mismatch"):
            self.simulate("child")

    def test_modified_blob_refused(self):
        with self.assertRaisesRegex(ValueError, "Pinned upstream blob mismatch"):
            self.simulate("blob")

    def test_redirect_refused(self):
        with self.assertRaisesRegex(ValueError, "Unexpected upstream response URL"):
            self.simulate("redirect")


if __name__ == "__main__":
    unittest.main()
