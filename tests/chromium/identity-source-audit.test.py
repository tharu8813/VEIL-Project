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
    def test_previously_observed_identity_blobs_are_required(self):
        expected = {
            "chrome/install_static/install_constants.h": "5791763ae4833c331c3e70a5d2768dfb580aeffb",
            "chrome/install_static/install_modes.cc": "bdd057bf4a266f30a3d9d93eef1ebcc1853bcf83",
            "chrome/install_static/policy_path_parser.cc": "2befa3a77fc70d92733c87abdede88fa30230319",
            "chrome/installer/util/shell_util.cc": "3710cb78f57047096c2f2651739a089f060cd6f8",
            "chrome/installer/util/install_util.cc": "c9b812b50eb9594318d0ab81b215d2f1730f9635",
            "chrome/browser/chrome_browser_main_win.cc": "77844751fad6de3fec9e1103176e29a994d1b24c",
        }
        for path, blob in expected.items():
            self.assertEqual(audit.FILES[path][0], blob)
        self.assertEqual(sum(blob is not None for blob, _ in audit.FILES.values()), 14)

    def test_discovery_paths_not_misrepresented_as_pinned(self):
        for path in (
            "chrome/install_static/user_data_dir.h",
            "chrome/app/chrome_main_delegate.cc",
            "chrome/browser/chrome_browser_main.cc",
            "chrome/common/chrome_paths.cc",
            "chrome/browser/policy/configuration_policy_handler_list_factory.cc",
            "chrome/elevation_service/elevation_service_idl.idl",
            "chrome/elevation_service/elevator.cc",
            "chrome/installer/setup/install.cc",
            "chrome/installer/setup/uninstall.cc",
            "chrome/browser/notifications/notification_platform_bridge_win.cc",
        ):
            self.assertIsNone(audit.FILES[path][0])

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
