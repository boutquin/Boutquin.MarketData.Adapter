import base64
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile

spec = importlib.util.spec_from_file_location('dependencies', Path(__file__).parents[1] / 'check-runner-dependencies.py')
check = importlib.util.module_from_spec(spec)
spec.loader.exec_module(check)


class PublishedDependencyTests(unittest.TestCase):
    def fixture(self, root):
        item = {'id': 'Boutquin.Storage.Domain', 'version': '2.0.0',
                'sourceRepository': 'https://github.com/boutquin/Boutquin.Storage', 'sourceCommit': 'published'}
        folder = root / item['id'].lower() / item['version']
        folder.mkdir(parents=True)
        archive = folder / 'boutquin.storage.domain.2.0.0.nupkg'
        with zipfile.ZipFile(archive, 'w') as z:
            z.writestr('package.nuspec', '<package><metadata><id>Boutquin.Storage.Domain</id><version>2.0.0</version>'
                       '<repository url="https://github.com/boutquin/Boutquin.Storage" commit="published"/></metadata></package>')
        item['sha256'] = hashlib.sha256(archive.read_bytes()).hexdigest()
        metadata = {'source': 'https://api.nuget.org/v3/index.json',
                    'contentHash': base64.b64encode(hashlib.sha512(archive.read_bytes()).digest()).decode()}
        (folder / '.nupkg.metadata').write_text(json.dumps(metadata))
        (folder / 'boutquin.storage.domain.2.0.0.nupkg.sha512').write_text(metadata['contentHash'])
        return item, folder, archive, metadata

    def test_accepts_exact_published_bytes(self):
        with tempfile.TemporaryDirectory() as d:
            item, folder, _, metadata = self.fixture(Path(d))
            self.assertEqual(folder, check.verify_cached_package(item, Path(d), metadata['source']))

    def test_rejects_old_same_version_archive(self):
        with tempfile.TemporaryDirectory() as d:
            item, folder, archive, metadata = self.fixture(Path(d))
            with zipfile.ZipFile(archive, 'a') as z:
                z.writestr('old-private-candidate.txt', 'same version, different bytes')
            metadata['contentHash'] = base64.b64encode(hashlib.sha512(archive.read_bytes()).digest()).decode()
            (folder / '.nupkg.metadata').write_text(json.dumps(metadata))
            with self.assertRaisesRegex(ValueError, 'SHA-256'):
                check.verify_cached_package(item, Path(d), metadata['source'])

    def test_rejects_private_source_even_for_exact_bytes(self):
        with tempfile.TemporaryDirectory() as d:
            item, folder, _, metadata = self.fixture(Path(d))
            metadata['source'] = str(Path(d) / 'private-feed')
            (folder / '.nupkg.metadata').write_text(json.dumps(metadata))
            with self.assertRaisesRegex(ValueError, 'package source'):
                check.verify_cached_package(item, Path(d), 'https://api.nuget.org/v3/index.json')

    def test_rejects_changed_content_hash(self):
        with tempfile.TemporaryDirectory() as d:
            item, folder, _, metadata = self.fixture(Path(d))
            metadata['contentHash'] = 'old'
            (folder / '.nupkg.metadata').write_text(json.dumps(metadata))
            with self.assertRaisesRegex(ValueError, 'content mismatch'):
                check.verify_cached_package(item, Path(d), metadata['source'])

    def test_signed_content_hash_is_separate_from_archive_hash(self):
        with tempfile.TemporaryDirectory() as d:
            item, folder, _, metadata = self.fixture(Path(d))
            item['contentHash'] = metadata['contentHash'] = 'pinned-signed-content'
            (folder / '.nupkg.metadata').write_text(json.dumps(metadata))
            self.assertEqual(folder, check.verify_cached_package(item, Path(d), metadata['source']))
            (folder / 'boutquin.storage.domain.2.0.0.nupkg.sha512').write_text('wrong-archive-hash')
            with self.assertRaisesRegex(ValueError, 'SHA-512'):
                check.verify_cached_package(item, Path(d), metadata['source'])


if __name__ == '__main__':
    unittest.main()
