#!/usr/bin/env python3
"""Exercise SourceLink coverage on real compiler-produced DLL/PDB bytes."""
import fcntl
import json
import os
from pathlib import Path
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
COMMIT = "a" * 40
VERSION = "2.0.0-test"
env = os.environ.copy()
env["NUGET_PACKAGES"] = str(ROOT / "artifacts/rc-build-cache")


def run(args):
    print("COMMAND:", " ".join(map(str, args)), flush=True)
    result = subprocess.run(list(map(str, args)), env=env, capture_output=True, text=True)
    print(result.stdout, end="", flush=True)
    print(result.stderr, end="", flush=True)
    print("EXIT:", result.returncode, flush=True)
    return result


with (ROOT / "artifacts/rc-build.lock").open("a") as lock:
    fcntl.flock(lock, fcntl.LOCK_EX)
    built = run(["dotnet", "build", ROOT / "scripts/package-inspector/PackageInspector.csproj", "--configuration", "Release", "-p:UseSharedCompilation=false"])
    assert built.returncode == 0
    for mode in ("unmapped", "mapped", "embedded"):
        with tempfile.TemporaryDirectory(prefix="md-source-link-") as temporary:
            directory = Path(temporary)
            (directory / "Directory.Build.props").write_text("<Project />")
            (directory / "fixture.csproj").write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyVersion>2.0.0.0</AssemblyVersion><FileVersion>2.0.0.0</FileVersion><InformationalVersion>{VERSION}+{COMMIT}</InformationalVersion><DebugType>portable</DebugType><SourceLink>mapping.json</SourceLink><EmbedAllSources>{str(mode == "embedded").lower()}</EmbedAllSources></PropertyGroup></Project>')
            (directory / "Program.cs").write_text('System.Console.WriteLine("real portable PDB fixture");\n')
            key = str(directory.resolve()) + "/*" if mode == "mapped" else "/unrelated/*"
            (directory / "mapping.json").write_text(json.dumps({"documents": {key: f"https://raw.githubusercontent.com/boutquin/Boutquin.MarketData.Adapter/{COMMIT}/*"}}))
            built = run(["dotnet", "build", directory / "fixture.csproj", "--configuration", "Release", "-p:UseSharedCompilation=false"])
            assert built.returncode == 0
            checked = run(["dotnet", ROOT / "scripts/package-inspector/bin/Release/net10.0/PackageInspector.dll", directory / "bin/Release/net10.0/fixture.dll", directory / "bin/Release/net10.0/fixture.pdb", VERSION, COMMIT])
            if mode == "unmapped":
                assert checked.returncode != 0 and "neither embedded source nor SourceLink coverage" in checked.stderr
            else:
                assert checked.returncode == 0, mode
            print("PASS:", mode, flush=True)
