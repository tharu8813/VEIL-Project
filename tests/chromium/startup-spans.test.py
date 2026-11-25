# SPDX-License-Identifier: MPL-2.0
import importlib.util
import pathlib
import sys
import unittest

SCRIPT = pathlib.Path(__file__).resolve().parents[2] / "scripts/chromium/review-startup-spans.py"
sys.path.insert(0, str(SCRIPT.parent))
spec = importlib.util.spec_from_file_location("review_startup_spans", SCRIPT)
review = importlib.util.module_from_spec(spec)
spec.loader.exec_module(review)

class ReviewTests(unittest.TestCase):
    def test_exact_lines_and_context(self):
        rows = review.inspect_spans(b"before\n  anchor\nafter\n", ((1, 3, ("anchor",)),))
        self.assertEqual(rows[0]["lines"], [{"line": 1, "text": "before"}, {"line": 2, "text": "  anchor"}, {"line": 3, "text": "after"}])

    def test_missing_or_outside_anchor_rejected(self):
        with self.assertRaisesRegex(ValueError, "anchor missing"):
            review.inspect_spans(b"anchor\nother\n", ((2, 2, ("anchor",)),))

    def test_invalid_ranges_rejected(self):
        for spans in (((0, 1, ("x",)),), ((1, 3, ("x",)),), ((1, 2, ("x",)), (2, 2, ("x",))), ((1, 2, ()),)):
            with self.subTest(spans=spans), self.assertRaises(ValueError):
                review.inspect_spans(b"x\nx\n", spans)

    def test_oversize_window_rejected(self):
        with self.assertRaises(ValueError):
            review.inspect_spans(b"x\n" * 81, ((1, 81, ("x",)),))

if __name__ == "__main__":
    unittest.main()
