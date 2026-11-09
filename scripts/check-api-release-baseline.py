#!/usr/bin/env python3
"""Check shipped API fingerprints; reject pending declarations at release boundaries."""
import argparse
import hashlib
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]


def check(root, release=False):
    manifest = json.loads((root / 'api-release-baseline.json').read_text())
    expected = manifest['packages']
    projects = {p.parent.name: p for p in (root / 'src').glob('*/PublicAPI.Shipped.txt')}
    if len(expected) != 12 or projects.keys() != expected.keys():
        raise ValueError('API baseline must name exactly the twelve source packages')
    for name, project in sorted(projects.items()):
        raw = project.read_bytes()
        if not raw.startswith(b'#nullable enable\n'):
            raise ValueError(f'{project}: expected LF nullable header')
        if hashlib.sha256(raw).hexdigest() != expected[name]:
            raise ValueError(f'{project}: shipped API differs from {manifest["version"]} fingerprint')
        pending = [s for s in project.with_name('PublicAPI.Unshipped.txt').read_text().splitlines()
                   if s.strip() and not s.startswith('#')]
        if release and pending:
            raise ValueError(f'{name}: pending API entries at release boundary ({len(pending)})')
    return len(projects)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--release', action='store_true')
    parser.add_argument('--root', type=Path, default=ROOT)
    args = parser.parse_args(argv)
    try:
        count = check(args.root, args.release)
    except (ValueError, KeyError, OSError) as error:
        print(f'FAIL: {error}', file=sys.stderr)
        return 1
    print(f'PASS: {count} released API baselines' + ('; no pending entries' if args.release else ''))
    return 0


if __name__ == '__main__':
    sys.exit(main())
