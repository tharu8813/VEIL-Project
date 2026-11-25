# SPDX-License-Identifier: MPL-2.0
import hashlib
import importlib.util
import pathlib
import unittest

SCRIPT = pathlib.Path(__file__).resolve().parents[2] / "scripts/chromium/audit-identity-source.py"
spec = importlib.util.spec_from_file_location("audit_identity_source", SCRIPT)
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)

class AuditTests(unittest.TestCase):
    def test_exact_blob_and_lines(self):
        raw = b"first\nkProductPathName = VEIL\nthird\n"
        blob = hashlib.sha1(b"blob " + str(len(raw)).encode() + b"\0" + raw).hexdigest()
        row = audit.inspect_blob("synthetic", raw, blob, "kProductPathName")
        self.assertEqual(row["blob"], blob)
        self.assertEqual(row["matches"], [{"line": 2, "text": "kProductPathName = VEIL"}])
    def test_wrong_hash_rejected(self):
        with self.assertRaises(ValueError):
            audit.inspect_blob("synthetic", b"x\n", "0" * 40, "x")
    def test_bounded_matches(self):
        row = audit.inspect_blob("synthetic", b"hit\n" * 61, None, "hit")
        self.assertEqual(row["matchCount"], 61)
        self.assertEqual(len(row["matches"]), 60)
        self.assertTrue(row["matchesTruncated"])

if __name__ == "__main__":
    unittest.main()
