"""Execute release gates on altered source trees, including optimized Python."""
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

SCRIPT = Path(__file__).parents[1] / 'check-api-release-baseline.py'


class ApiReleaseBaselineTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        packages = {}
        for index in range(12):
            name = f'Package{index}'
            project = self.root / 'src' / name
            project.mkdir(parents=True)
            shipped = project / 'PublicAPI.Shipped.txt'
            shipped.write_bytes(b'#nullable enable\nExample.Type\n')
            (project / 'PublicAPI.Unshipped.txt').write_text('#nullable enable\n')
            packages[name] = hashlib.sha256(shipped.read_bytes()).hexdigest()
        (self.root / 'api-release-baseline.json').write_text(json.dumps({'version': '3.0.0', 'packages': packages}))

    def run_gate(self, *args):
        return subprocess.run([sys.executable, '-O', str(SCRIPT), '--root', str(self.root), *args],
                              capture_output=True, text=True)

    def test_accepts_release_baseline(self):
        self.assertEqual(0, self.run_gate('--release').returncode)

    def test_allows_future_development_but_rejects_pending_release(self):
        (self.root / 'src/Package0/PublicAPI.Unshipped.txt').write_text('#nullable enable\nNew.Type\n')
        self.assertEqual(0, self.run_gate().returncode)
        self.assertIn('pending API entries', self.run_gate('--release').stderr)
        self.assertNotEqual(0, self.run_gate('--release').returncode)

    def test_rejects_changed_shipped_api(self):
        with (self.root / 'src/Package0/PublicAPI.Shipped.txt').open('a') as stream:
            stream.write('Altered.Type\n')
        self.assertNotEqual(0, self.run_gate().returncode)

    def test_rejects_line_ending_drift(self):
        path = self.root / 'src/Package0/PublicAPI.Shipped.txt'
        path.write_bytes(path.read_bytes().replace(b'\n', b'\r\n'))
        self.assertNotEqual(0, self.run_gate().returncode)

    def test_rejects_unlisted_package(self):
        path = self.root / 'src/Unexpected'
        path.mkdir()
        (path / 'PublicAPI.Shipped.txt').write_text('#nullable enable\n')
        self.assertNotEqual(0, self.run_gate().returncode)

    def test_rejects_missing_package(self):
        (self.root / 'src/Package0/PublicAPI.Shipped.txt').unlink()
        self.assertNotEqual(0, self.run_gate().returncode)


if __name__ == '__main__':
    unittest.main()
