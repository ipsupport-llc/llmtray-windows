"""Tests of scripts/l10n.py (run in CI: python3 -m unittest scripts/test_l10n.py)."""
import pathlib
import sys
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).parent))
import l10n  # noqa: E402


class StringsFile(unittest.TestCase):
    def test_escapes(self):
        text = '/* c */ "a" = "\\UD83D\\UDE00 \\101 \\"q\\" \\n"; // x\n"b"="\\u00e9";'
        self.assertEqual(l10n.parse_strings(text), {"a": '😀 A "q" \n', "b": "é"})


class Conversion(unittest.TestCase):
    def test_specifiers(self):
        self.assertEqual(l10n.mac_to_net("%@ of %lld"), ("{0} of {1}", 2))
        self.assertEqual(l10n.mac_to_net("%2$@ then %1$@"), ("{1} then {0}", 2))
        self.assertEqual(l10n.mac_to_net("100%% sure"), ("100% sure", 0))
        self.assertEqual(l10n.mac_to_net("%.1f GB", keep_formats=True), ("{0:F1} GB", 1))
        self.assertEqual(l10n.mac_to_net("{x} %@"), ("{{x}} {0}", 1))
        self.assertEqual(l10n.mac_to_net("{x}"), ("{x}", 0))


class Placeholders(unittest.TestCase):
    def test_parsing(self):
        self.assertIsNone(l10n.placeholders("{0} {oops}"))
        self.assertIsNone(l10n.placeholders("a } b"))
        self.assertEqual(l10n.placeholders("{{{0}}} {1,10} {2:F1}"), [0, 1, 2])

    def test_matching(self):
        self.assertTrue(l10n.same_placeholders("{0} of {1}", "{1} из {0}"))
        self.assertFalse(l10n.same_placeholders("{0}", "{0} {1}"))
        self.assertFalse(l10n.same_placeholders("{0}", "{0} {oops}"))
        self.assertTrue(l10n.same_placeholders("Plain", "Просто"))
        self.assertFalse(l10n.same_placeholders("Plain", "Просто {0}"))


if __name__ == "__main__":
    unittest.main()
