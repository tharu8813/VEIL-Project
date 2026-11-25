# SPDX-License-Identifier: MPL-2.0
import importlib.util
import pathlib
import sys
import unittest

path = pathlib.Path(__file__).resolve().parents[2] / "scripts/chromium/generate-ui-name-candidate.py"
sys.path.insert(0, str(path.parent))
spec = importlib.util.spec_from_file_location("candidate", path)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

FIXTURE = b"""<grit>
<if expr="testing">
<message name="IDS_PRODUCT_NAME" translateable="false">Chrome for Testing</message>
<message name="IDS_SHORT_PRODUCT_NAME" translateable="false">Chrome for Testing</message>
</if>
<if expr="not testing">
<message name="IDS_PRODUCT_NAME" translateable="false">
  Chromium
</message>
<message name="IDS_SHORT_PRODUCT_NAME" translateable="false">Chromium</message>
<message name="IDS_OTHER" translateable="false">ChromiumOS</message>
</if>
</grit>
"""

class CandidateTest(unittest.TestCase):
    def test_exact_scoped_names(self):
        result = module.rewrite(FIXTURE)
        self.assertEqual(result.count(b"VEIL"), 2)
        self.assertEqual(result.count(b"Chrome for Testing"), 2)
        self.assertIn(b"ChromiumOS", result)
        self.assertEqual(result.replace(b"VEIL", b"Chromium"), FIXTURE)
        self.assertIn(b"diff --git a/chrome/app/chromium_strings.grd", module.patch_bytes(FIXTURE, result))

    def test_missing_and_duplicate_refused(self):
        for invalid in (FIXTURE.replace(b">Chromium</message>", b">Other</message>"),
                        FIXTURE.replace(b"</grit>", b'<message name="IDS_PRODUCT_NAME" translateable="false">Chromium</message></grit>'),
                        FIXTURE.replace(b'translateable="false">Chromium', b'translateable="true">Chromium')):
            with self.assertRaises(ValueError):
                module.rewrite(invalid)

if __name__ == "__main__":
    unittest.main()
