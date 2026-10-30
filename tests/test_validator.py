import importlib.util
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location('validator', Path(__file__).parents[1] / 'AutoScript/Resources/validate_script.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class ValidatorTests(unittest.TestCase):
    def test_exact_python_counts(self):
        value = 'I held the key.\n"Do not leave."'
        result = module.analyze(value)
        self.assertEqual((result['words'], result['characters'], result['paragraphs']), (7, len(value), 2))
        self.assertEqual(result['errors'], [])

    def test_source_unicode_count(self):
        result = module.analyze('Mẹ giữ chìa khóa.\nCon trở về.')
        self.assertEqual(result["words"], 7)
        self.assertEqual(result['paragraphs'], 2)

    def test_reject_each_rule(self):
        for value in ['', 'One.\n\nTwo.', ' Text.', 'I am 71.', 'Wait…', "I don't go.", 'Dr Smith.', 'word ' * 111, 'Stop... Now.', 'A - B.', "It's late."]:
            with self.subTest(value=value):
                self.assertTrue(module.analyze(value)['errors'])

    def test_possessives_and_hyphens_allowed(self):
        self.assertEqual(module.analyze("Walt's daughter-in-law was seventy-one.")['errors'], [])

    def test_crlf(self):
        self.assertEqual(module.analyze('One.\r\nTwo.')['characters'], 9)


if __name__ == '__main__':
    unittest.main()
