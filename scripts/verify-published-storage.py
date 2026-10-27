#!/usr/bin/env python3
"""Verify published Storage archive provenance, signatures and actual DLL versions/bytes."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import zipfile

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('dependency_check', ROOT / 'scripts/check-runner-dependencies.py')
check = importlib.util.module_from_spec(spec)
spec.loader.exec_module(check)


def verify(packages, outputs, runtime_only=False):
    subprocess.run(['dotnet', 'build', str(ROOT / 'scripts/package-inspector/PackageInspector.csproj'),
                    '-c', 'Release', '--nologo'], check=True, stdout=subprocess.DEVNULL)
    inspector = ROOT / 'scripts/package-inspector/bin/Release/net10.0/PackageInspector.dll'
    lock = json.loads((ROOT / 'dependency-source.json').read_text())
    identities, dll_hashes, archives = [], {}, []
    for item in lock['publishedPackages']:
        if not item['id'].startswith('Boutquin.Storage.'):
            continue
        folder = packages / item['id'].lower() / item['version'].lower()
        if runtime_only and item['id'].endswith('.SourceGenerator') and not folder.exists():
            continue  # PrivateAssets build tool is intentionally absent from package consumers.
        check.verify_cached_package(item, packages, item['packageSource'])
        archive_path = folder / (item['id'] + '.' + item['version'] + '.nupkg').lower()
        signature = subprocess.run(['dotnet', 'nuget', 'verify', str(archive_path), '--all'],
                                   check=True, capture_output=True, text=True)
        if 'Content hash: ' + item['contentHash'] not in signature.stdout:
            raise ValueError('Signed content hash differs: ' + item['id'])
        archives.append({'id': item['id'], 'sha256': item['sha256'], 'source': item['packageSource'],
                         'signatureVerification': signature.stdout})
        with zipfile.ZipFile(archive_path) as archive:
            names = [n for n in archive.namelist() if n.endswith('.dll') and Path(n).name.startswith('Boutquin.Storage.')]
            expected = {'Boutquin.Storage.SourceGenerator.dll', 'Boutquin.Storage.SourceGenerator.CodeFixes.dll'} if item['id'].endswith('.SourceGenerator') else {item['id'] + '.dll'}
            if {Path(n).name for n in names} != expected or len(names) != len(expected):
                raise ValueError('Unexpected published DLL inventory: ' + item['id'])
            for name in names:
                path = folder / name
                content = path.read_bytes()
                if content != archive.read(name):
                    raise ValueError('Extracted DLL differs from signed archive: ' + name)
                identity = json.loads(subprocess.check_output(['dotnet', str(inspector), '--assembly-only',
                                    str(path), item['version'], item['sourceCommit']], text=True))
                digest = hashlib.sha256(content).hexdigest()
                dll_hashes[path.name] = digest
                identities.append({**identity, 'sha256': digest, 'archiveEntry': name})
    runtime_copies = []
    for path in outputs.rglob('Boutquin.Storage.*.dll'):
        relative = path.relative_to(outputs)
        # Only built runtime outputs; caches, old candidates and compiler intermediates are excluded.
        if 'bin' not in relative.parts or 'Release' not in relative.parts or 'artifacts' in relative.parts or 'obj' in relative.parts:
            continue
        if path.name not in dll_hashes or hashlib.sha256(path.read_bytes()).hexdigest() != dll_hashes[path.name]:
            raise ValueError('Runtime copy differs from published DLL: ' + str(path))
        runtime_copies.append(str(relative))
    required_runtime = {'Boutquin.Storage.Domain.dll', 'Boutquin.Storage.Infrastructure.dll'}
    if not required_runtime.issubset({Path(p).name for p in runtime_copies}):
        raise ValueError('Missing built runtime Storage copies')
    return {'status': 'PASS', 'archives': archives, 'assemblies': identities, 'runtimeCopies': runtime_copies,
            'privateBuildToolsMayBeAbsent': runtime_only}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--packages', required=True, type=Path)
    parser.add_argument('--outputs', required=True, type=Path)
    parser.add_argument('--report', required=True, type=Path)
    parser.add_argument('--runtime-only', action='store_true')
    args = parser.parse_args()
    report = verify(args.packages.resolve(), args.outputs.resolve(), args.runtime_only)
    args.report.parent.mkdir(parents=True, exist_ok=True)
    with args.report.open('x') as stream:
        json.dump(report, stream, indent=2)
        stream.write('\n')
    print(f"PASS: {len(report['assemblies'])} published DLLs are assembly/file version 2.0.0.0; {len(report['runtimeCopies'])} runtime copies match signed archives")


if __name__ == '__main__':
    main()
