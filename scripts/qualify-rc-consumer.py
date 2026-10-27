#!/usr/bin/env python3
"""Run the complete Adapter test suite against immutable packages in a cold consumer."""
import argparse
import runpy
import base64
import hashlib
import json
import os
import re
from pathlib import Path
import shutil
import subprocess
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
SOLUTION = 'Boutquin.MarketData.Adapter.slnx'


def require(condition, message):
    if not condition:
        raise ValueError(message)


def run(command, cwd, output, env):
    with output.open('x') as log:
        result = subprocess.run(command, cwd=cwd, env=env, stdout=log, stderr=subprocess.STDOUT)
    require(result.returncode == 0, f'Exit {result.returncode}: {command}; see {output}')


def verify_hashes(feed, expected):
    actual = {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in feed.iterdir() if p.suffix in ('.nupkg', '.snupkg')}
    require(actual == expected, 'Candidate archive inventory or hashes changed')


def prepare(consumer, version):
    consumer.mkdir()
    shutil.copytree(ROOT / 'tests', consumer / 'tests', ignore=shutil.ignore_patterns('bin', 'obj', 'TestResults', 'packages.lock.json'))
    for name in ('Directory.Build.props', 'Directory.Packages.props', 'global.json', '.editorconfig'):
        shutil.copy2(ROOT / name, consumer / name)
    solution = ET.parse(ROOT / SOLUTION)
    for folder in list(solution.getroot()):
        if folder.get('Name') == '/src/':
            solution.getroot().remove(folder)
    solution.write(consumer / SOLUTION, encoding='unicode')
    ids = set()
    for project in (consumer / 'tests').rglob('*.csproj'):
        tree = ET.parse(project)
        for group in tree.getroot():
            for reference in list(group):
                if reference.tag == 'ProjectReference':
                    name = 'Boutquin.MarketData.Adapter.' + Path(reference.get('Include').replace('\\', '/')).stem
                    ids.add(name)
                    reference.tag = 'PackageReference'
                    reference.attrib.clear()
                    reference.set('Include', name)
                    reference.set('VersionOverride', version)
        if project.name == 'Integration.csproj':
            group = ET.SubElement(tree.getroot(), 'ItemGroup')
            ET.SubElement(group, 'PackageReference', Include='Boutquin.MarketData.Adapter.Shared', VersionOverride=version)
            ET.SubElement(group, 'None', Include='package-inspection.json', CopyToOutputDirectory='PreserveNewest')
        tree.write(project, encoding='unicode')
    require(len(ids) == 12, 'Consumer must reference all 12 Adapter packages')
    return ids


def include_packaged_documentation(consumer, feed, inspection):
    """Expose the exact shipped XML to contract tests; NuGet omits it from runtime copies."""
    project = consumer / 'tests/Boutquin.MarketData.Adapter.Tests.Unit/Unit.csproj'
    directory = project.parent / 'CandidateDocumentation'
    directory.mkdir()
    for package in inspection['packages']:
        name = package['id']
        archive = feed / f"{name}.{inspection['version']}.nupkg"
        with zipfile.ZipFile(archive) as zipped:
            candidates = [path for path in zipped.namelist() if path == f'lib/net10.0/{name}.xml']
            require(len(candidates) == 1, f'Missing exact shipped XML: {name}')
            (directory / f'{name}.xml').write_bytes(zipped.read(candidates[0]))
    tree = ET.parse(project)
    group = ET.SubElement(tree.getroot(), 'ItemGroup')
    ET.SubElement(group, 'None', Include='CandidateDocumentation/*.xml', Link='%(Filename)%(Extension)', CopyToOutputDirectory='PreserveNewest')
    tree.write(project, encoding='unicode')


def verify_restore(consumer, cache, feed, dependencies, inspection, published=False):
    locked = json.loads((ROOT / 'dependency-source.json').read_text())
    expected = {item['id']: (item['version'], dependencies / locked.get('packageDirectory', 'nuget') / item['file']) for item in locked['packages']}
    expected.update({item['id']: (inspection['version'], feed / f"{item['id']}.{inspection['version']}.nupkg") for item in inspection['packages']})
    public = {item['id']: item for item in locked.get('publishedPackages', [])}
    public_check = runpy.run_path(str(ROOT / 'scripts/check-runner-dependencies.py'))['verify_cached_package']
    resolved = set()
    for project in (consumer / 'tests').rglob('*.csproj'):
        assets = json.loads((project.parent / 'obj/project.assets.json').read_text())
        for identity, library in assets['libraries'].items():
            require(library['type'] != 'project', 'Project reference leaked into package-only consumer')
            name, version = identity.rsplit('/', 1)
            if name.startswith(('Boutquin.MarketData.', 'Boutquin.Storage.')):
                if name in public:
                    require(version == public[name]['version'], 'Wrong published package version')
                    public_check(public[name], cache, public[name]['packageSource'])
                    resolved.add(name)
                    continue
                require(name in expected, f'Unpinned private package {identity}')
                required, archive = expected[name]
                require(version == required, f'Wrong private package version {identity}')
                folder = cache / name.lower() / version.lower()
                metadata = json.loads((folder / '.nupkg.metadata').read_text())
                require(metadata['source'] == 'https://api.nuget.org/v3/index.json' if published else Path(metadata['source']).resolve() == archive.parent.resolve(), f'Wrong source {identity}')
                content = archive.read_bytes()
                expected_content_hash = inspection['publishedContentHashes'][name] if published else base64.b64encode(hashlib.sha512(content).digest()).decode()
                require(metadata['contentHash'] == expected_content_hash, f'Wrong resolved hash {identity}')
                cached_archive = folder / f'{name.lower()}.{version.lower()}.nupkg'
                require(cached_archive.read_bytes() == content, f'Wrong cached bytes {identity}')
                resolved.add(name)
    require({p['id'] for p in inspection['packages']} <= resolved, 'Not every Adapter package resolved')
    return sorted(resolved)


def source_header():
    return (ROOT / 'tests/Boutquin.MarketData.Adapter.Tests.Integration/KernelPackageIdentityTests.cs').read_text().split('using System.Reflection;')[0]


def write_readme_examples(integration):
    snippets = re.findall(r'```csharp\n(.*?)```', (ROOT / 'README.md').read_text(), re.S)
    require(len(snippets) == 2, 'Expected both README quick-start snippets')
    (integration / 'CompiledReadmeExamples.cs').write_text(source_header() + 'using Boutquin.MarketData.Abstractions.Contracts;\nusing Boutquin.MarketData.Abstractions.ReferenceData;\nusing Boutquin.MarketData.Abstractions.Records;\nusing Boutquin.MarketData.Abstractions.Requests;\nusing Boutquin.MarketData.Adapter.Fred;\nusing Boutquin.MarketData.Adapter.NewYorkFed;\nusing Boutquin.MarketData.Adapter.Tiingo;\nusing Boutquin.MarketData.DependencyInjection;\nusing Microsoft.Extensions.DependencyInjection;\nnamespace Boutquin.MarketData.Adapter.Tests.Integration;\ninternal static class CompiledReadmeExamples\n{\n    internal static async Task Example(IServiceCollection services, Dictionary<string, string> configuration, IDataPipeline pipeline, DateRange dateRange, CancellationToken cancellationToken)\n    {\n' + '\n'.join(snippets) + '\n    }\n}\n')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--feed', type=Path, required=True)
    parser.add_argument('--inspection', type=Path, required=True)
    parser.add_argument('--work', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--published', action='store_true', help='Restore the signed Adapter RC only from NuGet.org')
    args = parser.parse_args()
    feed, work, output = args.feed.resolve(), args.work.resolve(), args.output.resolve()
    inspection = json.loads(args.inspection.read_text())
    require(inspection['status'] == 'PASS', 'Package inspection did not pass')
    require(subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip() == inspection['sourceCommit'], 'Consumer tests must come from frozen package source')
    verify_hashes(feed, inspection['sha256'])
    work.mkdir(parents=True, exist_ok=False)
    output.mkdir(parents=True, exist_ok=False)
    consumer = work / 'consumer'
    prepare(consumer, inspection['version'])
    include_packaged_documentation(consumer, feed, inspection)
    integration = consumer / 'tests/Boutquin.MarketData.Adapter.Tests.Integration'
    write_readme_examples(integration)
    shutil.copyfile(args.inspection, integration / 'package-inspection.json')
    (integration / 'FrozenAdapterIdentityTests.cs').write_text(source_header() + '''using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;
namespace Boutquin.MarketData.Adapter.Tests.Integration;
public sealed class FrozenAdapterIdentityTests
{
    [Fact]
    public void Every_loaded_adapter_matches_qualified_package_bytes()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "package-inspection.json")));
        var root = document.RootElement;
        var expected = root.GetProperty("version").GetString() + "+" + root.GetProperty("sourceCommit").GetString();
        var count = 0;
        foreach (var package in root.GetProperty("packages").EnumerateArray())
        {
            var assembly = Assembly.Load(package.GetProperty("id").GetString()!);
            Assert.Equal("2.0.0.0", assembly.GetName().Version!.ToString());
            Assert.Equal("2.0.0.0", System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly.Location).FileVersion);
            Assert.Equal(expected, assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);
            Assert.Equal(package.GetProperty("dllSha256").GetString(), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))).ToLowerInvariant());
            count++;
        }
        Assert.Equal(12, count);
    }
}
''')
    dependencies = ROOT / 'artifacts/dependency-source'
    config = ET.Element('configuration')
    sources = ET.SubElement(config, 'packageSources')
    ET.SubElement(sources, 'clear')
    for key, value in ([('nuget.org', 'https://api.nuget.org/v3/index.json')] if args.published else [('candidate', str(feed)), ('nuget.org', 'https://api.nuget.org/v3/index.json')]):
        ET.SubElement(sources, 'add', key=key, value=value)
    mappings = ET.SubElement(config, 'packageSourceMapping')
    for key, patterns in ([('nuget.org', ['*'])] if args.published else [('candidate', ['Boutquin.MarketData.Adapter.*']), ('nuget.org', ['*'])]):
        source = ET.SubElement(mappings, 'packageSource', key=key)
        for pattern in patterns:
            ET.SubElement(source, 'package', pattern=pattern)
    ET.ElementTree(config).write(consumer / 'nuget.config', encoding='unicode')
    env = os.environ.copy()
    cache = work / 'packages'
    http = work / 'http-cache'
    cache.mkdir(); http.mkdir()
    env.update(NUGET_PACKAGES=str(cache), NUGET_HTTP_CACHE_PATH=str(http))
    run(['python3', str(ROOT / 'scripts/check-runner-dependencies.py')], ROOT, output / 'dependency-bytes.log', env)
    run(['dotnet', 'restore', SOLUTION, '--configfile', 'nuget.config', '--no-http-cache', '-p:RestorePackagesWithLockFile=true', '-p:_WorkloadLibraryPacksFolder=' + str(work / 'absent-sdk-feed')], consumer, output / 'restore.log', env)
    resolved = verify_restore(consumer, cache, feed, dependencies, inspection, args.published)
    run(['dotnet', 'restore', SOLUTION, '--configfile', 'nuget.config', '--no-http-cache', '--locked-mode', '-p:_WorkloadLibraryPacksFolder=' + str(work / 'absent-sdk-feed')], consumer, output / 'locked-restore.log', env)
    run(['dotnet', 'test', SOLUTION, '-c', 'Release', '--no-restore', '--logger', 'trx', '--results-directory', str(output / 'trx')], consumer, output / 'tests.log', env)
    verify_restore(consumer, cache, feed, dependencies, inspection, args.published)
    run(['python3', str(ROOT / 'scripts/verify-dependency-identities.py'), '--packages', str(cache), '--outputs', str(consumer), '--report', str(output / 'dependency-identities.json')], ROOT, output / 'dependency-identities.log', env)
    verify_hashes(feed, inspection['sha256'])
    counters = []
    for trx in (output / 'trx').glob('*.trx'):
        count = ET.parse(trx).find('.//{*}Counters').attrib
        require(int(count['failed']) == 0 and int(count['notExecuted']) == 0 and int(count['total']) > 0, f'Non-green test lane: {trx}')
        counters.append(dict(file=trx.name, **count))
    require(len(counters) == 3, 'Expected three test project results')
    for lock in (consumer / 'tests').rglob('packages.lock.json'):
        target = output / 'locks' / lock.relative_to(consumer)
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(lock, target)
    (output / 'report.json').write_text(json.dumps(dict(status='PASS', sourceCommit=inspection['sourceCommit'], version=inspection['version'], resolvedPrivatePackages=resolved, counters=counters, candidateHashesUnchanged=True), indent=2) + '\n')
    print(f'PASS: package-only consumer, {sum(int(c["total"]) for c in counters)} tests, original archive bytes unchanged')

if __name__ == '__main__':
    main()
