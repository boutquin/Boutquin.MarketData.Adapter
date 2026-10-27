#!/usr/bin/env python3
"""Verify exact kernel DLLs and signed published Storage DLLs, including runtime copies."""
import argparse
import hashlib
import json
from pathlib import Path
import runpy
import subprocess
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--packages', required=True, type=Path)
    parser.add_argument('--outputs', required=True, type=Path)
    parser.add_argument('--report', required=True, type=Path)
    args = parser.parse_args()
    cache, outputs = args.packages.resolve(), args.outputs.resolve()
    lock = json.loads((ROOT / 'dependency-source.json').read_text())
    # Adapter does not consume the generator. Restore it only in an isolated metadata probe
    # so the same four published Storage DLL identities can be inspected independently.
    probe = not (cache / 'boutquin.storage.sourcegenerator/2.0.0').exists()
    if probe:
        (ROOT / 'artifacts').mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(prefix='storage-identity-probe-', dir=ROOT / 'artifacts') as temp:
            path = Path(temp)
            (path / 'Directory.Build.props').write_text('<Project />')
            (path / 'probe.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup><ItemGroup><PackageReference Include="Boutquin.Storage.SourceGenerator" Version="[2.0.0]" /></ItemGroup></Project>')
            subprocess.run(['dotnet', 'restore', str(path / 'probe.csproj'), '--packages', str(cache),
                            '--configfile', str(ROOT / 'nuget.ci.config'), '--no-http-cache',
                            '-p:_WorkloadLibraryPacksFolder=' + str(path / 'absent-sdk-feed')], check=True, stdout=subprocess.DEVNULL)
    storage = runpy.run_path(str(ROOT / 'scripts/verify-published-storage.py'))['verify'](cache, outputs)
    inspector = ROOT / 'scripts/package-inspector/bin/Release/net10.0/PackageInspector.dll'
    checker = runpy.run_path(str(ROOT / 'scripts/check-runner-dependencies.py'))
    identities, hashes = [], {}
    with tempfile.TemporaryDirectory(prefix='kernel-identity-') as temp:
        for item in lock['publishedPackages']:
            if not item['id'].startswith('Boutquin.MarketData.'):
                continue
            archive_path = cache / item['id'].lower() / item['version'].lower() / item['file'].lower()
            if not archive_path.exists():
                # Inspect the complete kernel inventory even when a package is not a runtime dependency.
                import urllib.request
                archive_path.parent.mkdir(parents=True, exist_ok=True)
                with urllib.request.urlopen(item['downloadUrl']) as response:
                    archive_path.write_bytes(response.read())
            checker['verify_archive'](item, archive_path)
            with zipfile.ZipFile(archive_path) as archive:
                name = item['id'] + '.dll'
                content = archive.read('lib/net10.0/' + name)
                path = Path(temp) / name
                path.write_bytes(content)
                identity = json.loads(subprocess.check_output(['dotnet', str(inspector), '--assembly-only', str(path), item['version'], item['sourceCommit']], text=True))
                hashes[name] = hashlib.sha256(content).hexdigest()
                identities.append({**identity, 'sha256': hashes[name]})
    copies = []
    for path in outputs.rglob('Boutquin.MarketData.*.dll'):
        relative = path.relative_to(outputs)
        if path.name.startswith('Boutquin.MarketData.Adapter.') or 'bin' not in relative.parts or 'Release' not in relative.parts or 'artifacts' in relative.parts or 'obj' in relative.parts:
            continue
        if path.name not in hashes or hashlib.sha256(path.read_bytes()).hexdigest() != hashes[path.name]:
            raise ValueError('Runtime kernel DLL differs from qualified archive: ' + str(path))
        copies.append(str(relative))
    if not copies or len(identities) != 12:
        raise ValueError('Missing kernel DLL identity inventory')
    report = {'status': 'PASS', 'kernelAssemblies': identities, 'kernelRuntimeCopies': copies,
              'storage': storage, 'unusedStorageGeneratorRestoredOnlyForMetadataProbe': probe}
    args.report.parent.mkdir(parents=True, exist_ok=True)
    with args.report.open('x') as stream:
        json.dump(report, stream, indent=2)
        stream.write('\n')
    print(f'PASS: 12 qualified kernel and 4 published Storage DLLs have assembly/file version 2.0.0.0; {len(copies)} kernel and {len(storage["runtimeCopies"])} Storage runtime copies match')


if __name__ == '__main__':
    main()
