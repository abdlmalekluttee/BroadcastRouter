"""Offline tests of the preparation boundary; no SDK or DeckLink hardware."""
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import tarfile
import tempfile
import unittest
from unittest.mock import patch as environment_patch

import recipe


class RecipeTests(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory()
        self.addCleanup(self.scratch.cleanup)
        self.root = Path(self.scratch.name)
        self.archive = self.root / "source.tar.xz"
        self.destination = self.root / "prepared"
        self.patch = self.root / "change.patch"
        self.patch.write_text("diff --git a/feature.c b/feature.c\n--- a/feature.c\n+++ b/feature.c\n@@ -1 +1 @@\n-before\n+after\n", encoding="utf-8", newline="\n")
        self.make_archive()

    def make_archive(self, name="ffmpeg-test/feature.c", data=b"before\n", link=False):
        with tarfile.open(self.archive, "w:xz") as archive:
            entry = tarfile.TarInfo(name)
            if link:
                entry.type = tarfile.SYMTYPE
                entry.linkname = "../escape"
                archive.addfile(entry)
            else:
                entry.size = len(data)
                archive.addfile(entry, io.BytesIO(data))
        self.lock = {"sourceDirectory": "ffmpeg-test", "sourceSha256": hashlib.sha256(self.archive.read_bytes()).hexdigest(),
                     "patchSha256": hashlib.sha256(self.patch.read_bytes()).hexdigest()}

    def prepare(self):
        return recipe.prepare_source(self.archive, self.destination, self.lock, self.patch)

    def test_modified_archive_refused_before_destination_creation(self):
        self.archive.write_bytes(self.archive.read_bytes() + b"tampered")
        with self.assertRaisesRegex(ValueError, "source hash"):
            self.prepare()
        self.assertFalse(self.destination.exists())

    def test_existing_output_preserved(self):
        self.destination.mkdir()
        keep = self.destination / "keep"
        keep.write_text("operator-owned")
        with self.assertRaisesRegex(ValueError, "already exists"):
            self.prepare()
        self.assertEqual("operator-owned", keep.read_text())

    def test_tar_escape_refused(self):
        self.make_archive("../escape")
        with self.assertRaisesRegex(ValueError, "archive member"):
            self.prepare()
        self.assertFalse((self.root / "escape").exists())

    def test_tar_absolute_path_refused(self):
        self.make_archive("/escape")
        with self.assertRaisesRegex(ValueError, "archive member"):
            self.prepare()

    def test_tar_link_refused(self):
        self.make_archive(link=True)
        with self.assertRaisesRegex(ValueError, "archive member"):
            self.prepare()

    def test_wrong_patch_fails_closed_without_fabricated_manifest(self):
        self.patch.write_text("--- a/absent\n+++ b/absent\n@@ -1 +1 @@\n-wrong\n+after\n")
        self.lock["patchSha256"] = hashlib.sha256(self.patch.read_bytes()).hexdigest()
        with self.assertRaisesRegex(ValueError, "patch"):
            self.prepare()
        self.assertFalse((self.destination / "source-provenance.json").exists())

    def test_missing_patch_pin_refused_before_destination_creation(self):
        del self.lock["patchSha256"]
        with self.assertRaisesRegex(ValueError, "patch hash"):
            self.prepare()
        self.assertFalse(self.destination.exists())

    def test_malformed_patch_pin_refused_before_destination_creation(self):
        self.lock["patchSha256"] = "not-a-sha256"
        with self.assertRaisesRegex(ValueError, "patch hash"):
            self.prepare()
        self.assertFalse(self.destination.exists())

    def test_applicable_but_modified_patch_refused(self):
        self.patch.write_text(self.patch.read_text().replace("+after", "+tampered"), newline="\n")
        with self.assertRaisesRegex(ValueError, "patch hash"):
            self.prepare()
        self.assertFalse(self.destination.exists())

    def test_preparation_inside_git_worktree_actually_applies_patch(self):
        subprocess.run(["git", "init", "--quiet", str(self.root)], check=True)
        source = self.prepare()
        self.assertEqual("after\n", (source / "feature.c").read_text(),
                         "enclosing Git worktree silently skipped the patch")

    def test_inherited_git_environment_cannot_redirect_patch(self):
        enclosing = self.root / "enclosing"
        subprocess.run(["git", "init", "--quiet", str(enclosing)], check=True)
        unrelated = enclosing / "feature.c"
        unrelated.write_text("before\n", newline="\n")
        with environment_patch.dict(os.environ, {"GIT_DIR": str(enclosing / ".git"), "GIT_WORK_TREE": str(enclosing)}):
            source = self.prepare()
        self.assertEqual("after\n", (source / "feature.c").read_text(),
                         "inherited Git environment redirected or skipped the patch")
        self.assertEqual("before\n", unrelated.read_text())

    def test_real_patch_changes_export_and_records_inputs(self):
        source = self.prepare()
        self.assertTrue((source / "feature.c").exists(), "patched source file was not produced")
        manifest = json.loads((self.destination / "source-provenance.json").read_text())
        self.assertEqual(self.lock["sourceSha256"], manifest["sourceSha256"])
        self.assertEqual(hashlib.sha256(self.patch.read_bytes()).hexdigest(), manifest["patchSha256"])
        self.assertEqual("after\n", (source / "feature.c").read_text())

    def test_toolchain_mismatch_refused(self):
        with self.assertRaisesRegex(ValueError, "toolchain"):
            recipe.verify_toolchain({"gcc": "1", "make": "2"}, {"gcc": "3", "make": "2"})

    def test_missing_dependency_refused(self):
        with self.assertRaisesRegex(ValueError, "toolchain"):
            recipe.verify_toolchain({"gcc": "1", "make": "2"}, {"gcc": "1"})

    def test_matching_toolchain_permits_unrelated_package(self):
        recipe.verify_toolchain({"gcc": "1"}, {"gcc": "1", "unrelated": "2"})


if __name__ == "__main__":
    unittest.main()
