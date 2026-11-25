# SPDX-License-Identifier: MPL-2.0
import base64
import hashlib
import io
import pathlib
import ssl
import sys
import unittest
import urllib.error

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2] / "scripts/chromium"))
from pinned_gitiles import fetch_pinned_blob

REV = "1" * 40
RAW = b"fixture\n"
BLOB = hashlib.sha1(b"blob " + str(len(RAW)).encode() + b"\0" + RAW).hexdigest()
PATH = "chrome/test.txt"

class Response(io.BytesIO):
    def __init__(self, url, raw=RAW, encoded=None):
        self.url = url
        super().__init__(base64.b64encode(raw) if encoded is None else encoded)
    def geturl(self):
        return self.url

class PinnedGitilesTests(unittest.TestCase):
    def test_transient_retry_then_verified_result(self):
        calls, waits = [], []
        def opener(url, timeout):
            calls.append(url)
            if len(calls) < 3:
                raise urllib.error.HTTPError(url, 503, "Unavailable", {}, io.BytesIO())
            return Response(url)
        self.assertEqual(fetch_pinned_blob(REV, PATH, BLOB, 128, opener=opener, sleeper=waits.append), RAW)
        self.assertEqual(len(calls), 3)
        self.assertEqual(waits, [1, 2])

    def test_retry_exhaustion_is_failure(self):
        calls, waits = [], []
        def opener(url, timeout):
            calls.append(url)
            raise urllib.error.HTTPError(url, 503, "Unavailable", {}, io.BytesIO())
        with self.assertRaises(urllib.error.HTTPError):
            fetch_pinned_blob(REV, PATH, BLOB, 128, opener=opener, sleeper=waits.append)
        self.assertEqual(len(calls), 3)
        self.assertEqual(waits, [1, 2])

    def test_permanent_http_and_certificate_errors_not_retried(self):
        for error in (lambda u: urllib.error.HTTPError(u, 404, "Missing", {}, io.BytesIO()),
                      lambda u: urllib.error.URLError(ssl.SSLCertVerificationError("certificate"))):
            calls, waits = [], []
            def opener(url, timeout):
                calls.append(url)
                raise error(url)
            with self.assertRaises(urllib.error.URLError):
                fetch_pinned_blob(REV, PATH, BLOB, 128, opener=opener, sleeper=waits.append)
            self.assertEqual(len(calls), 1)
            self.assertEqual(waits, [])

    def test_bad_payloads_fail_without_retry(self):
        for response in (lambda u: Response(u, b"wrong\n"),
                         lambda u: Response(u, encoded=b"not base64!"),
                         lambda u: Response(u, encoded=b"A" * 129),
                         lambda u: Response("https://example.invalid/")):
            calls, waits = [], []
            def opener(url, timeout):
                calls.append(url)
                return response(url)
            with self.assertRaises(ValueError):
                fetch_pinned_blob(REV, PATH, BLOB, 128, opener=opener, sleeper=waits.append)
            self.assertEqual(len(calls), 1)
            self.assertEqual(waits, [])

    def test_invalid_inputs_do_not_access_network(self):
        def opener(*args, **kwargs):
            self.fail("unexpected network access")
        for revision, path, blob in (("main", PATH, BLOB), (REV, "../x", BLOB), (REV, PATH, "bad")):
            with self.assertRaises(ValueError):
                fetch_pinned_blob(revision, path, blob, 128, opener=opener)

if __name__ == "__main__":
    unittest.main()
