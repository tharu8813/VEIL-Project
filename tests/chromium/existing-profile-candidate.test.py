# SPDX-License-Identifier: MPL-2.0
import importlib.util
import pathlib
import sys
import unittest

SCRIPT = pathlib.Path(__file__).resolve().parents[2] / "scripts/chromium/generate-existing-profile-candidate.py"
sys.path.insert(0, str(SCRIPT.parent))
spec = importlib.util.spec_from_file_location("startup_candidate", SCRIPT)
candidate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(candidate)


class CandidateTests(unittest.TestCase):
    def test_branch_before_upstream_creation(self):
        raw = (candidate.ANCHOR + "  RecursiveDirectoryCreate(user_data_dir);\n"
               "  GetDefaultUserDataDirectory(mode, &user_data_dir);\n").encode()
        modified = candidate.rewrite(raw).decode()
        self.assertEqual(modified, raw.decode().replace(candidate.ANCHOR, candidate.REPLACEMENT))
        self.assertLess(modified.index("veil-dev-existing-profile-only"),
                        modified.index("RecursiveDirectoryCreate"))
        self.assertLess(modified.index("veil-dev-existing-profile-only"),
                        modified.index("GetDefaultUserDataDirectory"))
        self.assertEqual(modified.count("RecursiveDirectoryCreate"), 1)
        self.assertEqual(modified.count("GetDefaultUserDataDirectory"), 1)

    def test_missing_duplicate_and_already_modified_refused(self):
        for raw in (b"", (candidate.ANCHOR * 2).encode(),
                    candidate.REPLACEMENT.encode()):
            with self.subTest(raw=raw), self.assertRaises(ValueError):
                candidate.rewrite(raw)

    def test_patch_is_one_source_file(self):
        raw = candidate.ANCHOR.encode()
        patch = candidate.create_patch(raw, candidate.rewrite(raw)).decode()
        self.assertEqual(patch.count("diff --git "), 1)
        self.assertIn("--- a/" + candidate.REL, patch)
        self.assertIn("+++ b/" + candidate.REL, patch)


if __name__ == "__main__":
    unittest.main()
