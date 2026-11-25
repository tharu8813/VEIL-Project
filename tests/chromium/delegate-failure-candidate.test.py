# SPDX-License-Identifier: MPL-2.0
import importlib.util
import pathlib
import sys
import unittest

SCRIPT = pathlib.Path(__file__).resolve().parents[2] / "scripts/chromium/generate-delegate-failure-candidate.py"
sys.path.insert(0, str(SCRIPT.parent))
spec = importlib.util.spec_from_file_location("delegate_candidate", SCRIPT)
candidate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(candidate)


class DelegateTests(unittest.TestCase):
    def test_delegate_rewrite_and_no_create_branch(self):
        suffix = '''    CHECK(base::PathService::OverrideAndCreateIfNeeded(
        chrome::DIR_USER_DATA, user_data_dir, false, true));
  }
'''
        raw = (candidate.ORIGINAL + suffix).encode()
        changed = candidate.rewrite_delegate(raw).decode()
        self.assertEqual(changed, candidate.MODIFIED + suffix)
        branch = changed.split('if (command_line->HasSwitch(', 1)[1].split(
            "// In tests", 1)[0]
        self.assertIn("CHECK(got_user_data_dir);", branch)
        self.assertIn("CHECK(user_data_dir_buf[0] != 0);", branch)
        self.assertIn("CHECK(invalid_user_data_dir_buf[0] == 0);", branch)
        self.assertIn("false, false", branch)
        self.assertNotIn("false, true", branch)
        self.assertIn("return;", branch)

    def test_missing_duplicate_and_modified_refused(self):
        for raw in (b"", (candidate.ORIGINAL * 2).encode(),
                    candidate.MODIFIED.encode()):
            with self.assertRaises(ValueError):
                candidate.rewrite_delegate(raw)

    def test_composition_and_idempotence_refusal(self):
        early = candidate.load_candidate("generate-existing-profile-candidate.py")
        cache = candidate.load_candidate("generate-cache-failure-candidate.py")
        raw = (early.ANCHOR + "\n" + cache.ORIGINAL).encode()
        result = candidate.compose_early(raw, early, cache)
        self.assertEqual(result, (early.REPLACEMENT + "\n" + cache.MODIFIED).encode())
        with self.assertRaises(ValueError):
            candidate.compose_early(result, early, cache)


if __name__ == "__main__":
    unittest.main()
