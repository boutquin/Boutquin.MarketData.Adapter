"""Dependency verification rejects changed provenance, with project refs as a control."""
import base64
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile

SPEC = importlib.util.spec_from_file_location('checker', Path(__file__).parents[1] / 'check-runner-dependencies.py')
CHECKER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(CHECKER)

class RunnerDependencyTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.feed = self.root / 'feed'
        (self.feed / 'nuget').mkdir(parents=True)
        self.cache = self.root / 'cache'
        self.id = 'Boutquin.MarketData.Abstractions'
        self.version = '2.0.0-rc.test'
        self.commit = 'a' * 40
        content = f'<package><metadata><id>{self.id}</id><version>{self.version}</version><repository url="repo" commit="{self.commit}" /></metadata></package>'
        package = self.feed / 'nuget' / 'candidate.nupkg'
        with zipfile.ZipFile(package, 'w') as z:
            z.writestr('candidate.nuspec', content)
        self.archive = package.read_bytes()
        (self.feed / 'manifest.json').write_text('{}')
        self.lock = dict(revision=self.commit, manifestInputs=[dict(file='manifest.json', sha256=hashlib.sha256(b'{}').hexdigest())], packages=[dict(id=self.id, version=self.version, file=package.name, sha256=hashlib.sha256(self.archive).hexdigest(), sourceCommit=self.commit, sourceRepository='repo')])
        self.save_lock()
        (self.root / 'Directory.Packages.props').write_text(f'<Project><PropertyGroup><MarketDataKernelVersion>{self.version}</MarketDataKernelVersion><MarketDataKernelCommit>{self.commit}</MarketDataKernelCommit></PropertyGroup><ItemGroup><PackageVersion Include="{self.id}" Version="$(MarketDataKernelVersion)" /></ItemGroup></Project>')
        project = self.root / 'tests' / 'Test'
        (project / 'obj').mkdir(parents=True)
        (project / 'Test.csproj').write_text('<Project/>')
        self.assets = project / 'obj/project.assets.json'
        self.libraries = {self.id + '/' + self.version: {'type': 'package'}, 'Boutquin.MarketData.Adapter.Shared/1.0.0': {'type': 'project'}}
        self.save_assets()
        self.metadata = self.cache / self.id.lower() / self.version / '.nupkg.metadata'
        self.metadata.parent.mkdir(parents=True)
        self.metadata.write_text(json.dumps(dict(source=str(self.feed / 'nuget'), contentHash=base64.b64encode(hashlib.sha512(self.archive).digest()).decode())))
        (self.metadata.parent / f'{self.id.lower()}.{self.version}.nupkg').write_bytes(self.archive)
        (self.metadata.parent / f'{self.id.lower()}.{self.version}.nupkg.sha512').write_text(base64.b64encode(hashlib.sha512(self.archive).digest()).decode())
        self.root_patch = patch.object(CHECKER, 'ROOT', self.root)
        self.root_patch.start()
        self.addCleanup(self.root_patch.stop)
        self.git_patch = patch.object(CHECKER.subprocess, 'check_output', return_value=self.commit + '\n')
        self.git_patch.start()
        self.addCleanup(self.git_patch.stop)

    def save_lock(self):
        (self.root / 'dependency-source.json').write_text(json.dumps(self.lock))

    def save_assets(self):
        self.assets.write_text(json.dumps(dict(libraries=self.libraries)))

    def test_project_reference_is_not_downloaded_dependency(self):
        CHECKER.verify(self.feed, self.cache)

    def test_unknown_private_package_is_rejected(self):
        self.libraries['Boutquin.MarketData.Adapter.Shared/1.0.0']['type'] = 'package'
        self.save_assets()
        with self.assertRaisesRegex(ValueError, 'Unpinned'):
            CHECKER.verify(self.feed, self.cache)

    def test_changed_package_bytes_rejected(self):
        (self.feed / 'nuget/candidate.nupkg').write_bytes(self.archive + b'changed')
        with self.assertRaisesRegex(ValueError, 'SHA-256'):
            CHECKER.verify(self.feed)

    def test_changed_manifest_rejected(self):
        (self.feed / 'manifest.json').write_text('changed')
        with self.assertRaisesRegex(ValueError, 'manifest'):
            CHECKER.verify(self.feed)

    def test_wrong_central_pin_rejected(self):
        p = self.root / 'Directory.Packages.props'
        p.write_text(p.read_text().replace(self.version, '2.0.0-rc.wrong'))
        with self.assertRaisesRegex(ValueError, 'Central kernel pin'):
            CHECKER.verify(self.feed)

    def test_wrong_restore_feed_rejected(self):
        data = json.loads(self.metadata.read_text())
        data['source'] = str(self.root / 'wrong')
        self.metadata.write_text(json.dumps(data))
        with self.assertRaisesRegex(ValueError, 'source'):
            CHECKER.verify(self.feed, self.cache)

    def test_wrong_restored_hash_rejected(self):
        data = json.loads(self.metadata.read_text())
        data['contentHash'] = 'wrong'
        self.metadata.write_text(json.dumps(data))
        with self.assertRaisesRegex(ValueError, 'content mismatch'):
            CHECKER.verify(self.feed, self.cache)

    def test_dependency_directory_cannot_escape_checkout(self):
        self.lock['packageDirectory'] = '../outside'
        (self.root / 'dependency-source.json').write_text(json.dumps(self.lock))
        with self.assertRaisesRegex(ValueError, 'inside'):
            CHECKER.verify(self.feed)

    def test_manifest_cannot_escape_checkout(self):
        self.lock['manifestInputs'][0]['file'] = '../outside'
        (self.root / 'dependency-source.json').write_text(json.dumps(self.lock))
        with self.assertRaisesRegex(ValueError, 'inside'):
            CHECKER.verify(self.feed)

    def test_isolated_feed_accepts_only_exact_restored_source(self):
        import shutil
        alternate = self.feed / 'feeds/stable'
        alternate.mkdir(parents=True)
        shutil.copyfile(self.feed / 'nuget/candidate.nupkg', alternate / 'candidate.nupkg')
        self.lock['packageDirectory'] = 'feeds/stable'
        (self.root / 'dependency-source.json').write_text(json.dumps(self.lock))
        with self.assertRaisesRegex(ValueError, 'source'):
            CHECKER.verify(self.feed, self.cache)
        data = json.loads(self.metadata.read_text())
        data['source'] = str(alternate)
        self.metadata.write_text(json.dumps(data))
        CHECKER.verify(self.feed, self.cache)

if __name__ == '__main__':
    unittest.main()
