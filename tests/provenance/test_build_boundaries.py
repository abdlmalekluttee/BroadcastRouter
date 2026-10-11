"""Exported builds must not discover untracked ancestor MSBuild inputs."""
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[2] / "scripts" / "Get-ReleaseBuildProperties.ps1"


class BuildBoundaryTests(unittest.TestCase):
    def test_untracked_ancestor_targets_cannot_affect_exported_build(self):
        self.assertTrue(SCRIPT.is_file(), "Release build import boundaries are missing")
        with tempfile.TemporaryDirectory(prefix="router-boundary-") as fixture:
            root = Path(fixture)
            (root / "Directory.Build.targets").write_text(
                "<Project><PropertyGroup><AmbientLeak>untracked</AmbientLeak></PropertyGroup></Project>")
            source = root / "export"
            source.mkdir()
            project = source / "test.csproj"
            project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
                               '<TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>')
            # Prove this fixture reproduces the default ancestor-import problem.
            command = ["dotnet", "msbuild", str(project), "-nologo", "-getProperty:AmbientLeak"]
            before = subprocess.run(command, text=True, capture_output=True, check=True)
            self.assertEqual(before.stdout.strip(), "untracked")
            props = subprocess.run([
                shutil.which("pwsh") or "powershell.exe", "-NoProfile", "-Command",
                "& '" + str(SCRIPT).replace("'", "''") + "' -SourceRoot '"
                + str(source).replace("'", "''") + "' | ConvertTo-Json"
            ], text=True, capture_output=True, check=True)
            after = subprocess.run(command + json.loads(props.stdout),
                                   text=True, capture_output=True, check=True)
            self.assertEqual(after.stdout.strip(), "")


if __name__ == "__main__":
    unittest.main(verbosity=2)
