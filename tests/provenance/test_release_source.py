"""Publishing must use tracked commit inputs, never local-only files."""
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts" / "New-ReleaseSource.ps1"


class ReleaseSourceTests(unittest.TestCase):
    def setUp(self):
        self.workspace = tempfile.TemporaryDirectory(prefix="router-source-")
        self.addCleanup(self.workspace.cleanup)
        self.root = Path(self.workspace.name)
        self.repo = self.root / "repo"
        self.repo.mkdir()
        self.git("init", "--quiet")
        self.git("config", "user.email", "fixture@example.invalid")
        self.git("config", "user.name", "Synthetic Fixture")
        (self.repo / "src").mkdir()
        (self.repo / "docs").mkdir()
        (self.repo / "src" / "Tracked.cs").write_text("// tracked\n")
        (self.repo / "docs" / "public.md").write_text("public fixture\n")
        (self.repo / ".gitignore").write_text("ignored.cs\n")
        self.git("add", ".")
        self.git("commit", "--quiet", "-m", "synthetic baseline")
        self.commit = self.git("rev-parse", "HEAD").stdout.strip()
        self.destination = self.root / "export"

    def git(self, *args):
        return subprocess.run(["git", "-C", str(self.repo), *args],
                              text=True, capture_output=True, check=True)

    def export(self):
        self.assertTrue(SCRIPT.is_file(), "Tracked-only release source exporter is missing")
        return subprocess.run([
            shutil.which("pwsh") or "powershell.exe", "-NoProfile", "-File", str(SCRIPT),
            "-Repository", str(self.repo), "-Commit", self.commit,
            "-Destination", str(self.destination)
        ], text=True, capture_output=True, timeout=30)

    def test_untracked_source_and_private_docs_never_enter_export(self):
        (self.repo / "src" / "LocalOnly.cs").write_text("// local-only fixture\n")
        (self.repo / "src" / "ignored.cs").write_text("// ignored local fixture\n")
        (self.repo / "docs" / "local-only.md").write_text("private synthetic note\n")
        result = self.export()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(sorted(p.relative_to(self.destination).as_posix()
                                for p in self.destination.rglob("*") if p.is_file()),
                         [".gitignore", "docs/public.md", "src/Tracked.cs"])

    def test_export_uses_commit_content_not_working_tree_content(self):
        (self.repo / "src" / "Tracked.cs").write_text("// modified locally\n")
        result = self.export()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual((self.destination / "src" / "Tracked.cs").read_text(), "// tracked\n")

    def test_existing_destination_is_not_replaced(self):
        self.destination.mkdir()
        sentinel = self.destination / "sentinel.txt"
        sentinel.write_text("keep")
        self.assertNotEqual(self.export().returncode, 0)
        self.assertEqual(sentinel.read_text(), "keep")


if __name__ == "__main__":
    unittest.main(verbosity=2)
