# SPDX-License-Identifier: MPL-2.0
"""Bounded canonical Gitiles fetch with mandatory pinned Git blob validation."""
import base64
import hashlib
import re
import time
import urllib.error
import urllib.request

BASE = "https://chromium.googlesource.com/chromium/src/+/"
TRANSIENT_HTTP = {408, 429, 500, 502, 503, 504}
MAX_ATTEMPTS = 3


def fetch_pinned_blob(revision, path, expected, max_encoded_bytes, *, opener=None, sleeper=None):
    if not re.fullmatch(r"[0-9a-f]{40}", revision) or not re.fullmatch(r"[0-9a-f]{40}", expected):
        raise ValueError("Expected exact revision and blob identities")
    if not re.fullmatch(r"[A-Za-z0-9_./-]+", path) or any(
        part in ("", ".", "..") for part in path.split("/")
    ):
        raise ValueError("Invalid upstream relative path")
    if not isinstance(max_encoded_bytes, int) or not 0 < max_encoded_bytes <= 8 * 1024 * 1024:
        raise ValueError("Invalid response size limit")
    opener = opener or urllib.request.urlopen
    sleeper = sleeper or time.sleep
    url = BASE + revision + "/" + path + "?format=TEXT"
    for attempt in range(1, MAX_ATTEMPTS + 1):
        try:
            with opener(url, timeout=20) as response:
                if response.geturl() != url:
                    raise ValueError("Unexpected upstream response URL")
                encoded = response.read(max_encoded_bytes + 1)
        except urllib.error.HTTPError as error:
            error.close()
            if error.code not in TRANSIENT_HTTP or attempt == MAX_ATTEMPTS:
                raise
        except (TimeoutError, ConnectionError):
            if attempt == MAX_ATTEMPTS:
                raise
        except urllib.error.URLError as error:
            if not isinstance(error.reason, (TimeoutError, ConnectionError)) or attempt == MAX_ATTEMPTS:
                raise
        else:
            if len(encoded) > max_encoded_bytes:
                raise ValueError("Unexpected upstream response size")
            raw = base64.b64decode(encoded.strip(), validate=True)
            blob = hashlib.sha1(b"blob " + str(len(raw)).encode("ascii") + b"\0" + raw).hexdigest()
            if blob != expected:
                raise ValueError("Pinned upstream blob mismatch")
            return raw
        sleeper(2 ** (attempt - 1))
    raise AssertionError("Unreachable retry state")
