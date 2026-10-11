"""Exported builds must not discover untracked ancestor MSBuild inputs."""
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[2] / "scripts" / "Get-ReleaseBuildProperties.ps1"


class BuildBoundaryTests(unittest.TestCase):
    def properties(self, source):
        props = subprocess.run([
            shutil.which("pwsh") or "powershell.exe", "-NoProfile", "-Command",
            "& '" + str(SCRIPT).replace("'", "''") + "' -SourceRoot '"
            + str(source).replace("'", "''") + "' | ConvertTo-Json"
        ], text=True, capture_output=True, check=True)
        return json.loads(props.stdout)

    def test_razor_payload_is_identical_in_two_export_directories(self):
        with tempfile.TemporaryDirectory(prefix="router-razor-") as fixture:
            payloads = []
            for label in ("export-one", "export-two"):
                source = Path(fixture) / label
                source.mkdir()
                project = source / "Synthetic.csproj"
                project.write_text('<Project Sdk="Microsoft.NET.Sdk.Razor"><PropertyGroup>'
                    '<TargetFramework>net8.0</TargetFramework><RootNamespace>Synthetic</RootNamespace>'
                    '</PropertyGroup><ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App" />'
                    '</ItemGroup></Project>')
                (source / "Card.razor").write_text('<h1>Synthetic product</h1>\n')
                result = subprocess.run(["dotnet", "build", str(project), "-c", "Release", "--nologo",
                    "-p:ContinuousIntegrationBuild=true", "-p:PathMap=" + str(source) + "=/_/",
                    *self.properties(source)], text=True, capture_output=True, timeout=60)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                payloads.append((source / "bin/Release/net8.0/Synthetic.dll").read_bytes())
            self.assertEqual(payloads[0], payloads[1], "Razor release payload depends on export path")

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
            after = subprocess.run(command + self.properties(source),
                                   text=True, capture_output=True, check=True)
            self.assertEqual(after.stdout.strip(), "")


if __name__ == "__main__":
    unittest.main(verbosity=2)
