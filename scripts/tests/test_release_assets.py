import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("release", Path(__file__).parents[1] / "verify-release-assets.py")
check = importlib.util.module_from_spec(spec)
spec.loader.exec_module(check)

class ReleaseAssetTests(unittest.TestCase):
    def test_frozen_bytes_source_tag_and_manifest_are_required(self):
        with tempfile.TemporaryDirectory() as d:
            feed = Path(d)
            package = feed / "example.nupkg"
            package.write_bytes(b"qualified")
            identity = {"version": "2.0.0-rc.20261027.1", "sourceCommit": "a" * 40,
                        "sha256": {package.name: hashlib.sha256(package.read_bytes()).hexdigest()}}
            manifest = feed / "package-hashes.json"
            manifest.write_text(json.dumps(identity))
            digest = hashlib.sha256(manifest.read_bytes()).hexdigest()
            tag = "v" + identity["version"]
            self.assertEqual(manifest, check.validate(feed, tag, digest, "a" * 40))
            for invalid_tag, invalid_hash, invalid_commit in [("v2.0.0", digest, "a" * 40),
                    (tag, "b" * 64, "a" * 40), (tag, digest, "b" * 40)]:
                with self.assertRaises(ValueError):
                    check.validate(feed, invalid_tag, invalid_hash, invalid_commit)
            package.write_bytes(b"changed")
            with self.assertRaisesRegex(ValueError, "Changed release archive"):
                check.validate(feed, tag, digest, "a" * 40)
