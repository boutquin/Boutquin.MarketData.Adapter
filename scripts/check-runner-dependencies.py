#!/usr/bin/env python3
"""Verify pinned runner package bytes, identities, revisions and restore provenance."""

import argparse
import base64
import hashlib
import json
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def verify_archive(item, path):
    content = path.read_bytes()
    if hashlib.sha256(content).hexdigest() != item["sha256"]:
        raise ValueError(f"Package SHA-256 mismatch: {item['id']}")
    with zipfile.ZipFile(path) as archive:
        names = [n for n in archive.namelist() if n.endswith(".nuspec")]
        if len(names) != 1:
            raise ValueError("Expected exactly one package nuspec")
        elements = {el.tag.rsplit("}", 1)[-1]: el for el in ET.fromstring(archive.read(names[0])).iter()}
    if (elements["id"].text, elements["version"].text) != (item["id"], item["version"]):
        raise ValueError(f"Package identity mismatch: {item['id']}")
    repository = elements["repository"]
    if (repository.get("url"), repository.get("commit")) != (item["sourceRepository"], item["sourceCommit"]):
        raise ValueError(f"Package source revision mismatch: {item['id']}")
    return content


def verify_cached_package(item, packages, source):
    folder = packages / item["id"].lower() / item["version"].lower()
    content = verify_archive(item, folder / f"{item['id']}.{item['version']}.nupkg".lower())
    metadata = json.loads((folder / ".nupkg.metadata").read_text(encoding="utf-8"))
    actual = metadata["source"]
    matches = actual == source if source.startswith("https://") else Path(actual).resolve() == Path(source).resolve()
    if not matches:
        raise ValueError(f"Unexpected resolved package source: {item['id']}: {actual}")
    archive_hash = base64.b64encode(hashlib.sha512(content).digest()).decode()
    if (folder / f"{item['id']}.{item['version']}.nupkg.sha512".lower()).read_text().strip() != archive_hash:
        raise ValueError(f"Cached archive SHA-512 mismatch: {item['id']}")
    # Signed package content excludes the signature; the full archive hash includes it.
    if metadata["contentHash"] != item.get("contentHash", archive_hash):
        raise ValueError(f"Resolved package content mismatch: {item['id']}")
    return folder



def verify_mappings():
    for name in ('nuget.config', 'nuget.ci.config'):
        config = ET.parse(ROOT / name)
        mappings = {e.get('key'): {p.get('pattern') for p in e.findall('package')}
                    for e in config.findall('.//packageSourceMapping/packageSource')}
        if mappings != {'nuget.org': {'*'}} or {e.get('key'): e.get('value') for e in config.findall('.//packageSources/add')} != {'nuget.org': 'https://api.nuget.org/v3/index.json'}:
            raise ValueError('Source mappings must use only NuGet.org for published dependencies')


def verify(feed, packages=None):
    lock = json.loads((ROOT / "dependency-source.json").read_text(encoding="utf-8"))
    if lock.get("publishedPackages"):
        verify_mappings()
    if lock.get("packages", [True]):
        head = subprocess.check_output(
            ["git", "-C", str(feed), "rev-parse", "HEAD"], text=True
        ).strip()
        if head != lock["revision"]:
            raise ValueError("Dependency source commit differs from the pinned revision")
        directory = (feed / lock.get("packageDirectory", "nuget")).resolve()
        if not directory.is_relative_to(feed.resolve()):
            raise ValueError("Dependency directory must remain inside the pinned checkout")
        for item in lock["manifestInputs"]:
            if not (feed / item["file"]).resolve().is_relative_to(feed.resolve()):
                raise ValueError("Manifest must remain inside the pinned checkout")
            if hashlib.sha256((feed / item["file"]).read_bytes()).hexdigest() != item["sha256"]:
                raise ValueError("Original dependency manifest differs from pinned bytes")
    else:
        head = "nuget.org"
        directory = feed
    central = ET.parse(ROOT / "Directory.Packages.props")
    version = central.findtext(".//MarketDataKernelVersion")
    commit = central.findtext(".//MarketDataKernelCommit")
    all_packages = lock["packages"] + lock.get("publishedPackages", [])
    declared = {item["id"]: item["version"] for item in all_packages}
    if len(declared) != len(all_packages):
        raise ValueError("Duplicate dependency identities")
    for item in central.iter("PackageVersion"):
        name = item.attrib["Include"]
        if name.startswith("Boutquin.MarketData."):
            pin = item.attrib["Version"].replace("$(MarketDataKernelVersion)", version)
            if declared.get(name) != pin:
                raise ValueError("Central kernel pin differs from qualified closure")
    if any(item["sourceCommit"] != commit for item in all_packages if item["id"].startswith("Boutquin.MarketData.")):
        raise ValueError("Central kernel commit differs from qualified source")
    files = {item["file"] for item in lock["packages"]}
    # Historical candidates remain available under their original immutable identities.
    if not files.issubset({p.name for p in directory.glob("*.nupkg")}):
        raise ValueError("Dependency source package inventory differs from the lock")
    resolved = set()
    if packages is not None:
        for project_root in (ROOT / "src", ROOT / "tests"):
            for project in project_root.rglob("*.csproj"):
                assets = json.loads((project.parent / "obj/project.assets.json").read_text(encoding="utf-8"))
                for identity, library in assets["libraries"].items():
                    if library.get("type") == "project":
                        continue
                    name, version = identity.rsplit("/", 1)
                    if name.startswith(("Boutquin.MarketData.", "Boutquin.Storage.")) and name not in declared:
                        raise ValueError(f"Unpinned private dependency: {identity}")
                    if name in declared:
                        if version != declared[name]:
                            raise ValueError(f"Unexpected resolved package version: {identity}")
                        resolved.add(name)
        if not resolved:
            raise ValueError("No pinned candidates found in solution restore assets")
    for item in lock["packages"]:
        if Path(item["file"]).name != item["file"]:
            raise ValueError("Package filename must be a basename")
        path = directory / item["file"]
        content = path.read_bytes()
        if hashlib.sha256(content).hexdigest() != item["sha256"]:
            raise ValueError(f"Package SHA-256 mismatch: {item['id']}")
        verify_archive(item, path)
        if packages is not None and item["id"] in resolved:
            verify_cached_package(item, packages, str(directory))
    for item in lock.get("publishedPackages", []):
        if item["packageSource"] != "https://api.nuget.org/v3/index.json":
            raise ValueError("Published Storage must use nuget.org")
        if packages is not None and item["id"] in resolved:
            verify_cached_package(item, packages, item["packageSource"])
    print(f"PASS: {len(declared)} original package identities and hashes; source {head}")
    if packages is not None:
        print(f"PASS: all {len(resolved)} referenced dependencies resolved from their verified sources")
        unused = sorted(set(declared) - resolved)
        if unused:
            print("Declared pins not referenced by this solution: " + ", ".join(unused))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--feed", type=Path, default=ROOT / "artifacts/dependency-source")
    parser.add_argument("--packages", type=Path)
    args = parser.parse_args()
    try:
        verify(args.feed.resolve(), args.packages)
    except (ValueError, OSError, KeyError, subprocess.CalledProcessError, zipfile.BadZipFile) as error:
        parser.exit(1, f"FAIL: {error}\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
