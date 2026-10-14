"""Pinned, offline source preparation. Never downloads or installs dependencies."""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import subprocess
import tarfile


def sha256(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def prepare_source(archive, destination, lock, patch):
    archive, destination, patch = Path(archive), Path(destination), Path(patch)
    if destination.exists():
        raise ValueError("destination already exists; no files changed")
    if sha256(archive) != lock["sourceSha256"]:
        raise ValueError("source hash mismatch; no files changed")
    patch_hash = sha256(patch)
    if lock.get("patchSha256") != patch_hash:
        raise ValueError("patch hash mismatch; no files changed")
    root = lock["sourceDirectory"]
    if not root or any(char in root for char in "/\\:") or root in (".", ".."):
        raise ValueError("invalid archive member root")
    with tarfile.open(archive, "r:xz") as source:
        members = source.getmembers()
        if len(members) > 30000 or sum(item.size for item in members) > 2_000_000_000:
            raise ValueError("archive member capacity exceeded")
        names = set()
        for member in members:
            path = PurePosixPath(member.name)
            if (not path.parts or path.is_absolute() or ".." in path.parts or path.parts[0] != root
                    or ":" in member.name or "\\" in member.name
                    or not (member.isfile() or member.isdir()) or member.name.casefold() in names):
                raise ValueError("unsafe archive member; no files extracted")
            names.add(member.name.casefold())
        destination.mkdir(parents=True)
        for member in members:
            target = destination.joinpath(*PurePosixPath(member.name).parts)
            if member.isdir():
                target.mkdir(parents=True, exist_ok=True)
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                with source.extractfile(member) as incoming, target.open("xb") as outgoing:
                    shutil.copyfileobj(incoming, outgoing)
                target.chmod(member.mode & 0o777)
    exported = destination / root
    # git-format patches otherwise silently skip paths beneath an enclosing checkout.
    # Do not inherit GIT_DIR/GIT_WORK_TREE/config overrides from the caller, either.
    git_environment = {key: value for key, value in os.environ.items() if not key.upper().startswith("GIT_")}
    git_environment["GIT_CEILING_DIRECTORIES"] = str(exported.resolve().parent)
    for args in (["--check"], []):
        result = subprocess.run(["git", "-c", "core.autocrlf=false", "apply", *args, str(patch.resolve())],
                                cwd=exported, env=git_environment, text=True, capture_output=True, timeout=30)
        if result.returncode:
            raise ValueError("source patch refused: " + result.stderr.strip())
    (destination / "source-provenance.json").write_text(json.dumps({
        "sourceSha256": lock["sourceSha256"], "patchSha256": patch_hash,
        "sourceDirectory": root, "qualification": "source-prepared-only; no hardware proof"
    }, indent=2) + "\n", encoding="utf-8")
    return exported


def verify_toolchain(expected, actual):
    differences = [name for name, version in expected.items() if actual.get(name) != version]
    if differences:
        raise ValueError("toolchain version mismatch: " + ", ".join(sorted(differences)))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", required=True, type=Path)
    parser.add_argument("--destination", required=True, type=Path)
    args = parser.parse_args()
    recipe = Path(__file__).resolve().parent
    lock = json.loads((recipe / "inputs.lock.json").read_text(encoding="utf-8"))
    print(prepare_source(args.archive, args.destination, lock, recipe / "win-safe-terminate.patch"))


if __name__ == "__main__":
    main()
