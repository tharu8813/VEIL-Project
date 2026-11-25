# SPDX-License-Identifier: MPL-2.0
import base64
import hashlib
import io
import urllib.error
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

    def test_transient_503_recovers_and_checks_hash(self):
        raw = b"identity\n"
        expected = hashlib.sha1(b"blob " + str(len(raw)).encode() + b"\0" + raw).hexdigest()
        calls, waits = [], []
        class Response(io.BytesIO):
            def geturl(self):
                return "https://chromium.googlesource.com/test"
        def opener(url, timeout):
            calls.append((url, timeout))
            if len(calls) < 3:
                raise urllib.error.HTTPError(url, 503, "Unavailable", {}, io.BytesIO())
            return Response(base64.b64encode(raw))
        row = audit.fetch_one("rev", "fixture", expected, "identity", opener=opener, sleeper=waits.append)
        self.assertEqual(row["status"], "SOURCE_OBSERVED")
        self.assertEqual(row["attempts"], 3)
        self.assertEqual(row["blob"], expected)
        self.assertEqual(waits, [1, 2])

    def test_503_exhaustion_still_requires_evidence(self):
        calls, waits = [], []
        def opener(url, timeout):
            calls.append(url)
            raise urllib.error.HTTPError(url, 503, "Unavailable", {}, io.BytesIO())
        row = audit.fetch_one("rev", "fixture", "0" * 40, "x", opener=opener, sleeper=waits.append)
        self.assertEqual(row["status"], "NOT_OBSERVED")
        self.assertTrue(row["required"])
        self.assertTrue(row["transient"])
        self.assertEqual(row["attempts"], 3)
        self.assertEqual(len(calls), 3)
        self.assertEqual(waits, [1, 2])

    def test_permanent_missing_path_not_retried(self):
        waits = []
        def opener(url, timeout):
            raise urllib.error.HTTPError(url, 404, "Missing", {}, io.BytesIO())
        row = audit.fetch_one("rev", "fixture", None, "x", opener=opener, sleeper=waits.append)
        self.assertEqual(row["status"], "NOT_OBSERVED")
        self.assertFalse(row["required"])
        self.assertFalse(row["transient"])
        self.assertEqual(row["attempts"], 1)
        self.assertEqual(waits, [])

    def test_bad_blob_never_retried(self):
        waits = []
        class Response(io.BytesIO):
            def geturl(self):
                return "https://chromium.googlesource.com/test"
        row = audit.fetch_one("rev", "fixture", "0" * 40, "x",
                              opener=lambda url, timeout: Response(base64.b64encode(b"x\n")),
                              sleeper=waits.append)
        self.assertEqual(row["status"], "NOT_OBSERVED")
        self.assertTrue(row["required"])
        self.assertFalse(row["transient"])
        self.assertEqual(row["attempts"], 1)
        self.assertEqual(waits, [])

if __name__ == "__main__":
    unittest.main()
