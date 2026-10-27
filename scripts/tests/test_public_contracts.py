"""Regression checks for previously missing release and documentation gates."""
import copy
import importlib.util
from pathlib import Path
import re
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]


def catalogue_errors(readme, guide, changelog, source):
    errors = []
    for registration in re.findall(r'IDataSourceAdapter<([^,]+),\s*([^>]+)>', source):
        record = registration[1].strip()
        row = next(line for line in readme.splitlines() if line.startswith('| `Boutquin.MarketData.Adapter.Tiingo`'))
        section = guide.split('## Tiingo ')[1].split('\n---')[0]
        if f'`{record}`' not in row or f'`{record}`' not in section:
            errors.append(record)
    if 'TiingoDividendHistoryAdapter' not in changelog.split('## [2.0.0-rc.20261027.2]')[1].split('\n## [')[0]:
        errors.append('changelog dividend')
    return errors


class PublicContractsTests(unittest.TestCase):
    def test_catalogue_covers_registered_tiingo_records_and_rejects_omission(self):
        texts = [(ROOT / p).read_text() for p in ('README.md', 'docs/adapters.md', 'CHANGELOG.md', 'src/Tiingo/ServiceCollectionExtensions.cs')]
        self.assertEqual([], catalogue_errors(*texts))
        changed = list(texts)
        changed[0] = changed[0].replace(', `DividendPayment`', '')
        self.assertIn('DividendPayment', catalogue_errors(*changed))

    def test_consumer_has_only_exact_package_references(self):
        spec = importlib.util.spec_from_file_location('consumer', ROOT / 'scripts/qualify-rc-consumer.py')
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        import tempfile
        with tempfile.TemporaryDirectory() as temporary:
            consumer = Path(temporary) / 'consumer'
            self.assertEqual(12, len(module.prepare(consumer, '2.0.0-rc.control')))
            self.assertFalse((consumer / 'src').exists())
            for project in consumer.rglob('*.csproj'):
                tree = ET.parse(project)
                self.assertEqual([], tree.findall('.//ProjectReference'))
                for item in tree.findall('.//PackageReference'):
                    if item.get('Include').startswith('Boutquin.MarketData.Adapter.'):
                        self.assertEqual('2.0.0-rc.control', item.get('VersionOverride'))

    def test_consumer_copies_exact_shipped_xml_and_rejects_missing_xml(self):
        import tempfile
        import zipfile
        spec = importlib.util.spec_from_file_location('consumer_docs', ROOT / 'scripts/qualify-rc-consumer.py')
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        for present in (True, False):
            with self.subTest(present=present), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                consumer = root / 'consumer'
                unit = consumer / 'tests/Boutquin.MarketData.Adapter.Tests.Unit'
                unit.mkdir(parents=True)
                project = unit / 'Unit.csproj'
                project.write_text('<Project />')
                name = 'Boutquin.MarketData.Adapter.Shared'
                with zipfile.ZipFile(root / f'{name}.2.0.0.nupkg', 'w') as archive:
                    archive.writestr(f'lib/net10.0/{name}.xml' if present else 'wrong.xml', b'<doc>exact shipped bytes</doc>')
                inspection = dict(version='2.0.0', packages=[dict(id=name)])
                if present:
                    module.include_packaged_documentation(consumer, root, inspection)
                    self.assertEqual(b'<doc>exact shipped bytes</doc>', (unit / 'CandidateDocumentation' / f'{name}.xml').read_bytes())
                    item = ET.parse(project).find('.//None')
                    self.assertEqual('PreserveNewest', item.get('CopyToOutputDirectory'))
                    self.assertEqual('%(Filename)%(Extension)', item.get('Link'))
                else:
                    with self.assertRaisesRegex(ValueError, 'Missing exact shipped XML'):
                        module.include_packaged_documentation(consumer, root, inspection)

if __name__ == '__main__':
    unittest.main()
