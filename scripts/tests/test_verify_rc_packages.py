"""Mutation tests for RC archive gate logic; actual DLL/PDB validation runs on the candidate."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile

SCRIPT = Path(__file__).resolve().parents[1] / "verify-rc-packages.py"
spec = importlib.util.spec_from_file_location("verify_rc_packages", SCRIPT)
verifier = importlib.util.module_from_spec(spec)
spec.loader.exec_module(verifier)
VERSION = "2.0.0-test"
COMMIT = "a" * 40
DEPENDENCY = "Boutquin.Storage.Infrastructure"
DEPENDENCY_VERSION = "2.0.0-evidence.20261026.3"


class PackageGateMutationTests(unittest.TestCase):
    def source(self, commit, path):
        self.assertEqual(COMMIT, commit)
        if path == "README.md":
            return b"candidate README"
        if path == "dependency-source.json":
            return json.dumps({"packages": [{"id": DEPENDENCY, "version": DEPENDENCY_VERSION}]}).encode()
        if path == "Directory.Packages.props":
            return f'<Project><ItemGroup><PackageVersion Include="{DEPENDENCY}" Version="{DEPENDENCY_VERSION}" /></ItemGroup></Project>'.encode()
        if path.endswith(".csproj"):
            reference = f'<PackageReference Include="{DEPENDENCY}" />' if "src/Shared/" in path else ""
            return f'<Project><ItemGroup>{reference}</ItemGroup></Project>'.encode()
        raise AssertionError(path)

    def run_fixture(self, mutation=None):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            for name in verifier.IDS:
                dependencies = f'<dependency id="{DEPENDENCY}" version="{DEPENDENCY_VERSION}" />' if name.endswith(".Shared") else ""
                if name.endswith(".Shared") and mutation == "missing":
                    dependencies = ""
                if name.endswith(".Shared") and mutation == "duplicate":
                    dependencies = f'<dependency id="{DEPENDENCY}" version="0.0.1" />' + dependencies
                if name.endswith(".Shared") and mutation == "unexpected":
                    dependencies += '<dependency id="Unknown.Runtime" version="1.0.0" />'
                metadata = f'<package><metadata><id>{name}</id><version>{VERSION}</version><repository commit="{COMMIT}" url="https://github.com/boutquin/Boutquin.MarketData.Adapter"/><license type="expression">Apache-2.0</license><readme>README.md</readme><dependencies><group targetFramework="net10.0">{dependencies}</group></dependencies></metadata></package>'
                with zipfile.ZipFile(root / f"{name}.{VERSION}.nupkg", "w") as archive:
                    archive.writestr(name + ".nuspec", metadata)
                    archive.writestr("README.md", b"candidate README")
                    archive.writestr(f"lib/net10.0/{name}.dll", b"DLL fixture")
                    archive.writestr(f"lib/net10.0/{name}.xml", b'<doc><members><member name="T:Fixture"><summary>Package gate fixture.</summary></member></members></doc>')
                with zipfile.ZipFile(root / f"{name}.{VERSION}.snupkg", "w") as archive:
                    symbol_metadata = metadata
                    if name.endswith(".Shared") and mutation == "symbol-id":
                        symbol_metadata = metadata.replace(f"<id>{name}</id>", "<id>Wrong.Package</id>")
                    if name.endswith(".Shared") and mutation == "symbol-version":
                        symbol_metadata = metadata.replace(f"<version>{VERSION}</version>", "<version>0.0.1</version>")
                    if name.endswith(".Shared") and mutation == "symbol-commit":
                        symbol_metadata = metadata.replace(COMMIT, "b" * 40)
                    archive.writestr(name + ".nuspec", symbol_metadata)
                    archive.writestr(f"lib/net10.0/{name}.pdb", b"PDB fixture")

            def inspect(args, text):
                name = Path(args[2]).stem
                if mutation == "assembly" and name.endswith(".Shared"):
                    name = "Boutquin.MarketData.Adapter.Tiingo"
                return json.dumps({"name": name})

            with patch.object(verifier, "source_bytes", side_effect=self.source), patch.object(verifier.subprocess, "check_output", side_effect=inspect):
                return verifier.verify(root, VERSION, COMMIT, root / "manifest.json", root / "inspector.dll")

    def test_valid_fixture(self):
        self.assertEqual("PASS", self.run_fixture()["status"])

    def test_required_dependency_cannot_disappear(self):
        with self.assertRaisesRegex(ValueError, "dependenc"):
            self.run_fixture("missing")

    def test_wrong_assembly_identity_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "assembly"):
            self.run_fixture("assembly")

    def test_duplicate_dependency_cannot_hide_a_wrong_version(self):
        with self.assertRaisesRegex(ValueError, "dependenc"):
            self.run_fixture("duplicate")

    def test_unexpected_runtime_dependency_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "dependenc"):
            self.run_fixture("unexpected")

    def test_symbol_archive_identity_is_checked(self):
        for mutation in ("symbol-id", "symbol-version", "symbol-commit"):
            with self.subTest(mutation=mutation), self.assertRaisesRegex(ValueError, "symbol"):
                self.run_fixture(mutation)


if __name__ == "__main__":
    unittest.main()
