from pathlib import Path
import subprocess
import sys
import unittest

OPS = Path(__file__).resolve().parents[2] / 'ops' / 'mir3-web'

class PatchOriginTests(unittest.TestCase):
    def test_build_requires_validated_origin_and_preserves_existing_config(self):
        text = (OPS / 'Mir3-Launcher.iss').read_text()
        self.assertIn('#ifndef PatchOrigin', text)
        self.assertIn('validate-patch-origin.py', text)
        self.assertIn('!= 0', text)
        self.assertIn('Section: "Patcher"; Key: "Host"; String: "{#PatchOrigin}/"', text)
        self.assertIn('createkeyifdoesntexist', text)
        self.assertIn("not FileExists(ExpandConstant('{app}\\Launcher.ini'))", text)

    def test_origin_validation(self):
        script = OPS / 'validate-patch-origin.py'
        self.assertTrue(script.exists(), 'build-time origin validator missing')
        for value in ('', 'http://patch.example.invalid', 'https://', 'https://u:p@patch.example.invalid', 'https://patch.example.invalid/path', 'https://patch.example.invalid?x=y', 'https://patch.example.invalid#x', 'https://patch.example.invalid:99999', 'https://bad_host', 'https://patch.example.invalid\nInjected=yes', 'https://patch.example.invalid/"'):
            result = subprocess.run([sys.executable, str(script), value], capture_output=True)
            self.assertNotEqual(result.returncode, 0, value)
        for value in ('https://patch.example.invalid', 'https://patch.example.invalid:8443'):
            result = subprocess.run([sys.executable, str(script), value], capture_output=True)
            self.assertEqual(result.returncode, 0, result.stderr)

if __name__ == '__main__':
    unittest.main()
