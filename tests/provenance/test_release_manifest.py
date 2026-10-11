"""Release provenance contract; fixtures never use operational data."""
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts" / "Write-ReleaseManifest.ps1"


class ReleaseManifestTests(unittest.TestCase):
    def setUp(self):
        self.workspace = tempfile.TemporaryDirectory(prefix="router-provenance-")
        self.addCleanup(self.workspace.cleanup)
        self.root = Path(self.workspace.name)
        self.package = self.root / "package"
        self.package.mkdir()
        (self.package / "app.dll").write_bytes(b"synthetic assembly fixture")
        (self.package / "assets").mkdir()
        (self.package / "assets" / "logo.svg").write_bytes(b"<svg/>")
        self.qualification = self.root / "qualification.json"
        self.qualification.write_text(json.dumps({
            "status": "not-qualified", "buildId": None, "sha256": None
        }), encoding="utf-8")

    def run_manifest(self, commit="a" * 40):
        if not SCRIPT.is_file():
            self.fail("Release manifest generator is missing")
        return subprocess.run([
            shutil.which("pwsh") or "powershell.exe", "-NoProfile", "-File", str(SCRIPT),
            "-PackageDirectory", str(self.package), "-Commit", commit,
            "-SdkVersion", "8.0.425", "-FFmpegQualificationFile", str(self.qualification)
        ], capture_output=True, text=True, timeout=30)

    def test_records_every_payload_digest_and_build_identity(self):
        result = self.run_manifest()
        self.assertEqual(result.returncode, 0, result.stderr)
        manifest = json.loads((self.package / "release-manifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual(manifest["commit"], "a" * 40)
        self.assertEqual(manifest["sdkVersion"], "8.0.425")
        self.assertEqual(manifest["ffmpeg"]["status"], "not-qualified")
        self.assertIsNone(manifest["ffmpeg"]["buildId"])
        files = manifest["files"]
        self.assertEqual([f["path"] for f in files], ["app.dll", "assets/logo.svg"])
        self.assertEqual(files[0]["sha256"], hashlib.sha256(b"synthetic assembly fixture").hexdigest())
        self.assertEqual(files[1]["sha256"], hashlib.sha256(b"<svg/>").hexdigest())
        self.assertEqual(files[0]["length"], len(b"synthetic assembly fixture"))

    def test_same_inputs_produce_identical_manifest_bytes(self):
        self.assertEqual(self.run_manifest().returncode, 0)
        path = self.package / "release-manifest.json"
        first = path.read_bytes()
        path.unlink()
        self.assertEqual(self.run_manifest().returncode, 0)
        self.assertEqual(path.read_bytes(), first)

    def test_existing_manifest_is_never_replaced(self):
        path = self.package / "release-manifest.json"
        path.write_bytes(b"do not replace")
        result = self.run_manifest()
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(path.read_bytes(), b"do not replace")

    def test_invalid_commit_is_rejected_without_manifest(self):
        self.assertNotEqual(self.run_manifest("not-a-commit").returncode, 0)
        self.assertFalse((self.package / "release-manifest.json").exists())

    def test_qualified_claim_requires_build_identity_and_digest(self):
        self.qualification.write_text('{"status":"qualified","buildId":null,"sha256":null}', encoding="utf-8")
        self.assertNotEqual(self.run_manifest().returncode, 0)
        self.assertFalse((self.package / "release-manifest.json").exists())


if __name__ == "__main__":
    unittest.main(verbosity=2)
