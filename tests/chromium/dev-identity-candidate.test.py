# SPDX-License-Identifier: MPL-2.0
import importlib.util
import pathlib
import sys
import unittest

path = pathlib.Path(__file__).resolve().parents[2] / "scripts/chromium/generate-dev-identity-candidate.py"
sys.path.insert(0, str(path.parent))
spec = importlib.util.spec_from_file_location("candidate", path)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
FIXTURE = b"""inline constexpr wchar_t kProductPathName[] = L"Chromium";
inline constexpr char kSafeBrowsingName[] = "chromium";
.base_app_name = L"Chromium",
.base_app_id = L"Chromium",
.browser_prog_id_prefix = L"ChromiumHTM",
.pdf_prog_id_prefix = L"ChromiumPDF",
.browser_prog_id_description =
    L"Chromium HTML Document",
.pdf_prog_id_description = L"Chromium PDF Document",
.app_guid = L"",
.direct_launch_url_scheme = "chromium",
.active_setup_guid =
    L"{7D2B3E1D-D096-4594-9D8F-A6667F12E0AC}",
.toast_activator_clsid = {0x635EFA6F},
.elevator_iid = {0xbb19a0e5},
"""

class IdentityTest(unittest.TestCase):
    def test_scoped_changes(self):
        result = module.rewrite(FIXTURE)
        self.assertEqual(result.count(b"VEIL"), 7)
        self.assertIn(b'.direct_launch_url_scheme = "veil-chromium-dev"', result)
        self.assertIn(b'L"{C0D85D7A-6F11-49E9-A4B1-2D8F6C11B7E3}"', result)
        self.assertIn(b'.toast_activator_clsid = {0x635EFA6F}', result)
        self.assertIn(b'.elevator_iid = {0xbb19a0e5}', result)
        self.assertNotIn(b'7D2B3E1D-D096-4594-9D8F-A6667F12E0AC', result)
        self.assertIn(b'kSafeBrowsingName[] = "chromium"', result)
        self.assertIn(b'.app_guid = L""', result)
        self.assertIn(b'kProductPathName[] = L"VEIL Chromium Dev"', result)
    def test_missing_duplicate_or_wrong_value(self):
        for raw in (FIXTURE.replace(b'.base_app_id', b'.other'),
                    FIXTURE + b'.base_app_id = L"Chromium",\n',
                    FIXTURE.replace(b'L"ChromiumPDF"', b'L"Other"'),
                    FIXTURE.replace(b'.direct_launch_url_scheme', b'.other_scheme'),
                    FIXTURE.replace(b'7D2B3E1D-D096-4594-9D8F-A6667F12E0AC', b'00000000-0000-0000-0000-000000000000')):
            with self.assertRaises(ValueError):
                module.rewrite(raw)

if __name__ == "__main__":
    unittest.main()
