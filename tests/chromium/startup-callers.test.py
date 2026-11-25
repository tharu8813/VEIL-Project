# SPDX-License-Identifier: MPL-2.0
import importlib.util
import pathlib
import sys
import unittest

SCRIPT = pathlib.Path(__file__).resolve().parents[2] / "scripts/chromium/review-startup-callers.py"
sys.path.insert(0, str(SCRIPT.parent))
spec = importlib.util.spec_from_file_location("startup_callers", SCRIPT)
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)


class CallerTests(unittest.TestCase):
    def test_context_merges_without_duplicate_lines(self):
        rows = ["ordinary"] * 40
        rows[0] = rows[5] = rows[39] = "CallAnchor"
        result = audit.collect("\n".join(rows).encode(), r"CallAnchor", ("CallAnchor",))
        self.assertEqual(result["hitLines"], [1, 6, 40])
        self.assertEqual([(w["first"], w["last"]) for w in result["windows"]],
                         [(1, 12), (34, 40)])
        for window in result["windows"]:
            self.assertEqual([x["line"] for x in window["lines"]],
                             list(range(window["first"], window["last"] + 1)))

    def test_missing_anchor_or_match_rejected(self):
        with self.assertRaises(ValueError):
            audit.collect(b"CallAnchor", r"CallAnchor", ("Other",))
        with self.assertRaises(ValueError):
            audit.collect(b"CallAnchor", r"Other", ("CallAnchor",))

    def test_bound_rejects_instead_of_truncating(self):
        raw = ("CallAnchor\n" * (audit.MAX_LINES + 1)).encode()
        with self.assertRaises(ValueError):
            audit.collect(raw, r"CallAnchor", ("CallAnchor",))


if __name__ == "__main__":
    unittest.main()
