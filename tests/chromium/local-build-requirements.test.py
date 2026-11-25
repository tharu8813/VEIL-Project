# SPDX-License-Identifier: MPL-2.0
import hashlib
import pathlib
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2] / "scripts/chromium"))
from observe_local_build_requirements import observe_local, LocalObjects


class LocalRequirementsTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name)
        self.git("init", "--object-format=sha1", "-q")
        self.files = {"DEPS": b"pre_deps_hooks = []\n", "docs/windows_build_instructions.md": b"Windows SDK fixture\n",
                      "build/vs_toolchain.py": b"raise RuntimeError('must never execute')\n# Visual Studio fixture\n",
                      "build/toolchain/win/setup_toolchain.py": b"# SDK_VERSION fixture\n"}
        for path, data in self.files.items():
            dest = self.root / path
            dest.parent.mkdir(parents=True, exist_ok=True)
            dest.write_bytes(data)
        self.git("add", ".")
        self.revision = self.commit()

    def git(self, *args, input=None):
        return subprocess.check_output(["git", "-C", str(self.root), *args], input=input)

    def commit(self):
        self.git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-qm", "fixture")
        return self.git("rev-parse", "HEAD").decode().strip()

    def test_pinned_objects_not_worktree_or_head(self):
        (self.root / "DEPS").write_bytes(b"altered worktree")
        self.git("add", "DEPS")
        self.commit()
        report = observe_local(self.root, self.revision)
        self.assertEqual(report["source"], "LOCAL_GIT_OBJECTS_ONLY")
        self.assertTrue(report["commitObjectHashVerified"])
        self.assertFalse(report["remoteOriginVerified"])
        self.assertEqual(report["build"], "NOT RUN")
        self.assertFalse(report["requirementsReviewed"])
        self.assertEqual({f["path"]: f["sha256"] for f in report["files"]},
                         {p: hashlib.sha256(b).hexdigest() for p, b in self.files.items()})

    def test_missing_nested_toolchain_file_refused(self):
        self.git("rm", "build/toolchain/win/setup_toolchain.py")
        changed = self.commit()
        with self.assertRaises(KeyError):
            observe_local(self.root, changed)

    def test_missing_commit_and_wrong_type_refused(self):
        with self.assertRaises(subprocess.CalledProcessError):
            observe_local(self.root, "1" * 40)
        tree = self.git("rev-parse", "HEAD^{tree}").decode().strip()
        with self.assertRaises(ValueError):
            observe_local(self.root, tree)
        with self.assertRaises(ValueError):
            observe_local(self.root, "HEAD")

    def test_symlink_and_replacement_refused_or_ignored(self):
        blob = self.git("hash-object", "-w", "--stdin", input=b"elsewhere").decode().strip()
        self.git("update-index", "--cacheinfo", "120000," + blob + ",DEPS")
        changed = self.commit()
        with self.assertRaises(ValueError):
            observe_local(self.root, changed)
        self.git("replace", self.revision, changed)
        report = observe_local(self.root, self.revision)
        self.assertEqual(report["revision"], self.revision)
        self.assertEqual(report["rootTree"], LocalObjects(self.root, self.revision).root)
        deps = next(f for f in report["files"] if f["path"] == "DEPS")
        self.assertEqual(deps["sha256"], hashlib.sha256(self.files["DEPS"]).hexdigest())


if __name__ == "__main__":
    unittest.main()
