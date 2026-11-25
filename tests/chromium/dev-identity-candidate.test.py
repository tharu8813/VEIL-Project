# SPDX-License-Identifier: MPL-2.0
import importlib.util
import pathlib
import unittest

path = pathlib.Path(__file__).resolve().parents[2] / "scripts/chromium/generate-dev-identity-candidate.py"
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
"""

class IdentityTest(unittest.TestCase):
    def test_scoped_changes(self):
        result = module.rewrite(FIXTURE)
        self.assertEqual(result.count(b"VEIL"), 7)
        self.assertIn(b'kSafeBrowsingName[] = "chromium"', result)
        self.assertIn(b'.app_guid = L""', result)
        self.assertIn(b'kProductPathName[] = L"VEIL Chromium Dev"', result)
    def test_missing_duplicate_or_wrong_value(self):
        for raw in (FIXTURE.replace(b'.base_app_id', b'.other'),
                    FIXTURE + b'.base_app_id = L"Chromium",\n',
                    FIXTURE.replace(b'L"ChromiumPDF"', b'L"Other"')):
            with self.assertRaises(ValueError):
                module.rewrite(raw)

if __name__ == "__main__":
    unittest.main()
