#!/usr/bin/env python3
"""License-free integrity and credential checks for the repository."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import subprocess
import sys
from datetime import datetime
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
PACKAGE = ROOT / "Packages" / "com.supabaseunity.client"
VERSION_PATTERN = re.compile(r"^\d+\.\d+\.\d+(?:-(?:alpha|beta|rc)\.\d+)?$")
GUID_PATTERN = re.compile(r"(?m)^guid: ([0-9a-f]{32})$")
PINNED_ACTION_PATTERN = re.compile(r"^[^@]+@[0-9a-f]{40}$")
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"
DEFAULT_RELEASE_VERIFICATION = ROOT / ".github" / "release-verification.json"
SHA256_PATTERN = re.compile(r"^[0-9a-f]{64}$")

LIVE_CREDENTIAL_PATTERNS = {
    "Supabase API key": re.compile(r"sb_(?:secret|publishable)_[A-Za-z0-9_-]{20,}"),
    "JWT": re.compile(
        r"eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}"
    ),
    "Supabase project URL": re.compile(r"https?://[a-z0-9]{16,}\.supabase\.co", re.I),
    "GitHub token": re.compile(r"gh[pousr]_[A-Za-z0-9]{20,}"),
    "API token": re.compile(r"\bsk-[A-Za-z0-9_-]{20,}\b"),
    "AWS access key": re.compile(r"\bAKIA[0-9A-Z]{16}\b"),
    "private key": re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----"),
}


def fail(message: str) -> None:
    print(f"::error::{message}")
    raise SystemExit(1)


def read_text(path: Path) -> str:
    try:
        return path.read_text(encoding="utf-8-sig")
    except UnicodeDecodeError as exception:
        fail(f"Release file is not UTF-8 text: {path.relative_to(ROOT)} ({exception})")
    raise AssertionError("unreachable")


def release_files() -> list[Path]:
    roots = [
        PACKAGE,
        ROOT / ".github",
        ROOT / "README.md",
        ROOT / "CONTRIBUTING.md",
        ROOT / "LICENSE.md",
        ROOT / "RELEASING.md",
        ROOT / "SECURITY.md",
        ROOT / "SUPPORT.md",
        ROOT / ".gitattributes",
        ROOT / ".gitignore",
    ]
    files: list[Path] = []
    for root in roots:
        if root.is_file():
            files.append(root)
        elif root.is_dir():
            files.extend(
                path
                for path in root.rglob("*")
                if path.is_file()
                and "__pycache__" not in path.parts
                and path.suffix not in {".pyc", ".pyo"}
            )
        else:
            fail(f"Required release path is missing: {root.relative_to(ROOT)}")

    return sorted(set(files))


def tracked_files() -> list[Path]:
    tracked = subprocess.run(
        ["git", "ls-files"], cwd=ROOT, check=True, capture_output=True, text=True
    ).stdout.splitlines()
    return sorted(ROOT / path for path in tracked if (ROOT / path).is_file())


def validate_package_manifest() -> str:
    manifest_path = PACKAGE / "package.json"
    manifest = json.loads(read_text(manifest_path))
    if manifest.get("name") != "com.supabaseunity.client":
        fail("package.json contains an unexpected package name")
    version = manifest.get("version", "")
    if not VERSION_PATTERN.fullmatch(version):
        fail("package.json version must be an explicit alpha SemVer")
    if manifest.get("unity") != "2021.3":
        fail("package.json must retain Unity 2021.3 as its minimum editor version")
    expected_tag = f"v{version}"
    for field in ("documentationUrl", "changelogUrl", "licensesUrl"):
        if expected_tag not in manifest.get(field, ""):
            fail(f"package.json {field} must point at immutable tag {expected_tag}")
    expected_install = (
        "https://github.com/DorukTan/unity-supabase.git"
        f"?path=/Packages/com.supabaseunity.client#{expected_tag}"
    )
    for path in (ROOT / "README.md", PACKAGE / "README.md", PACKAGE / "README.tr.md"):
        if expected_install not in read_text(path):
            fail(f"Install URL is missing or out of date in {path.relative_to(ROOT)}")
    return version


def validate_client_info(version: str) -> None:
    """The X-Client-Info version is hardcoded in C# and drifts from package.json."""
    http_source = PACKAGE / "Runtime" / "Internal" / "SupabaseHttp.cs"
    text = read_text(http_source)
    match = re.search(r'ClientInfo\s*=\s*"supabase-unity/([^"]+)"', text)
    if not match:
        fail("SupabaseHttp.ClientInfo could not be located")
    if match.group(1) != version:
        fail(
            "SupabaseHttp.ClientInfo reports "
            f"{match.group(1)} but package.json declares {version}"
        )


def validate_json_files(files: list[Path]) -> None:
    for path in files:
        if path.suffix not in {".json", ".asmdef"}:
            continue
        try:
            json.loads(read_text(path))
        except json.JSONDecodeError as exception:
            fail(f"Invalid JSON in {path.relative_to(ROOT)}: {exception}")


def validate_credentials_and_text(
    files: list[Path], *, check_trailing_whitespace: bool = True
) -> None:
    for path in files:
        relative = path.relative_to(ROOT)
        if path.is_symlink():
            fail(f"Symlinks are not allowed in the release boundary: {relative}")
        if path.stat().st_size > 2 * 1024 * 1024:
            fail(f"Release file exceeds 2 MiB: {relative}")
        if path.suffix.lower() == ".png":
            data = path.read_bytes()
            if not data.startswith(PNG_SIGNATURE):
                fail(f"Invalid PNG presentation asset: {relative}")
            printable = data.decode("latin-1")
            for label, pattern in LIVE_CREDENTIAL_PATTERNS.items():
                if pattern.search(printable):
                    fail(f"Possible live {label} found in binary asset {relative}")
            continue
        text = read_text(path)
        for label, pattern in LIVE_CREDENTIAL_PATTERNS.items():
            if pattern.search(text):
                fail(f"Possible live {label} found in {relative}")
        if check_trailing_whitespace:
            for line_number, line in enumerate(text.splitlines(), start=1):
                if line.rstrip(" \t") != line:
                    fail(f"Trailing whitespace in {relative}:{line_number}")

    tracked = subprocess.run(
        ["git", "ls-files"], cwd=ROOT, check=True, capture_output=True, text=True
    ).stdout.splitlines()
    settings_assets = [
        path for path in tracked if Path(path).name.lower() == "supabasesettings.asset"
    ]
    if settings_assets:
        fail("SupabaseSettings.asset must remain local and untracked")


def validate_unity_meta() -> None:
    guids: dict[str, Path] = {}
    for meta in PACKAGE.rglob("*.meta"):
        target = Path(str(meta)[:-5])
        if not target.exists():
            fail(f"Orphan Unity meta file: {meta.relative_to(ROOT)}")
        match = GUID_PATTERN.search(read_text(meta))
        if not match:
            fail(f"Unity meta file has no valid GUID: {meta.relative_to(ROOT)}")
        guid = match.group(1)
        if guid in guids:
            fail(
                "Duplicate Unity meta GUID in "
                f"{meta.relative_to(ROOT)} and {guids[guid].relative_to(ROOT)}"
            )
        guids[guid] = meta

    for path in PACKAGE.rglob("*"):
        if path.name.endswith(".meta") or any(part.endswith("~") for part in path.parts):
            continue
        if path == PACKAGE:
            continue
        meta = Path(str(path) + ".meta")
        if not meta.exists():
            fail(f"Unity asset is missing its meta file: {path.relative_to(ROOT)}")


def validate_release_verification(
    version: str,
    evidence_path: Path,
    archives_directory: Path | None,
) -> None:
    required = archives_directory is not None
    if not evidence_path.is_file():
        if required:
            fail(f"Release verification record is missing: {evidence_path}")
        return

    try:
        evidence = json.loads(read_text(evidence_path))
    except json.JSONDecodeError as exception:
        fail(f"Invalid release verification JSON in {evidence_path}: {exception}")

    if evidence.get("schemaVersion") != 1:
        fail("Release verification schemaVersion must be 1")

    recorded_version = evidence.get("packageVersion")
    if not isinstance(recorded_version, str) or not VERSION_PATTERN.fullmatch(recorded_version):
        fail("Release verification contains an invalid packageVersion")
    if required and recorded_version != version:
        fail(
            f"Release verification is for {recorded_version}, but package.json declares {version}"
        )

    verified_at = evidence.get("verifiedAtUtc")
    if not isinstance(verified_at, str) or not verified_at.endswith("Z"):
        fail("Release verification verifiedAtUtc must be an ISO-8601 UTC timestamp")
    try:
        datetime.fromisoformat(verified_at.replace("Z", "+00:00"))
    except ValueError:
        fail("Release verification verifiedAtUtc is not a valid timestamp")

    unity = evidence.get("unity")
    if not isinstance(unity, dict):
        fail("Release verification is missing its unity result")
    editor_version = unity.get("editorVersion")
    if not isinstance(editor_version, str) or not re.fullmatch(
        r"[0-9]+\.[0-9]+\.[0-9]+[abfp][0-9]+", editor_version
    ):
        fail("Release verification contains an invalid Unity editor version")

    tests = unity.get("editModeTests")
    if not isinstance(tests, dict):
        fail("Release verification is missing EditMode test results")
    expected_test_fields = {"total", "passed", "failed", "skipped", "inconclusive"}
    if set(tests) != expected_test_fields or any(
        not isinstance(tests[field], int) or tests[field] < 0 for field in expected_test_fields
    ):
        fail("Release verification contains invalid EditMode test counts")
    if (
        tests["total"] == 0
        or tests["passed"] != tests["total"]
        or tests["failed"] != 0
        or tests["skipped"] != 0
        or tests["inconclusive"] != 0
    ):
        fail("Release verification does not show a fully passing EditMode suite")
    if unity.get("cleanUnitypackageImport") != "passed":
        fail("Release verification does not show a passing clean .unitypackage import")
    if unity.get("webglBuild") != "passed":
        fail("Release verification does not show a passing WebGL build")

    archives = evidence.get("archives")
    if not isinstance(archives, dict) or len(archives) != 2:
        fail("Release verification must contain exactly two archive hashes")
    expected_names = {
        f"com.supabaseunity.client-{recorded_version}.tgz",
        f"com.supabaseunity.client-{recorded_version}.unitypackage",
    }
    if set(archives) != expected_names:
        fail("Release verification archive names do not match its package version")
    if any(
        not isinstance(digest, str) or not SHA256_PATTERN.fullmatch(digest)
        for digest in archives.values()
    ):
        fail("Release verification contains an invalid SHA-256 digest")

    if not required:
        return

    assert archives_directory is not None
    for name, expected_digest in archives.items():
        archive = archives_directory / name
        if not archive.is_file():
            fail(f"Verified release archive is missing: {archive}")
        actual_digest = hashlib.sha256(archive.read_bytes()).hexdigest()
        if actual_digest != expected_digest:
            fail(
                f"Release archive {name} does not match the locally verified archive. "
                "Run verify_release.py again before tagging."
            )


def validate_workflows() -> None:
    workflow_dir = ROOT / ".github" / "workflows"
    workflows = sorted([*workflow_dir.glob("*.yml"), *workflow_dir.glob("*.yaml")])
    for workflow in workflows:
        text = read_text(workflow)
        if re.search(r"(?m)^\s*pull_request_target\s*:", text):
            fail(f"Unsafe pull_request_target trigger in {workflow.relative_to(ROOT)}")
        for match in re.finditer(r"(?m)^\s*(?:-\s*)?uses:\s*([^\s#]+)", text):
            action = match.group(1)
            if action.startswith("./"):
                continue
            if not PINNED_ACTION_PATTERN.fullmatch(action):
                fail(f"GitHub Action is not pinned by commit SHA: {action}")

        for forbidden in ("UNITY_LICENSE", "UNITY_SERIAL", "game-ci/"):
            if forbidden in text:
                fail(
                    f"Hosted workflows must remain license-free; found {forbidden} in "
                    f"{workflow.relative_to(ROOT)}"
                )

    package_checks = read_text(workflow_dir / "package-checks.yml")
    if not package_checks.startswith("name: License-free package checks"):
        fail("package-checks.yml must state that hosted checks are license-free")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--release-archives", type=Path)
    parser.add_argument("--release-verification", type=Path)
    args = parser.parse_args()

    files = release_files()
    version = validate_package_manifest()
    validate_client_info(version)
    validate_json_files(files)
    validate_credentials_and_text(files)
    validate_credentials_and_text(tracked_files(), check_trailing_whitespace=False)
    validate_unity_meta()
    validate_workflows()
    evidence_path = (
        args.release_verification.resolve()
        if args.release_verification
        else DEFAULT_RELEASE_VERIFICATION
    )
    archives_directory = (
        args.release_archives.resolve() if args.release_archives else None
    )
    validate_release_verification(version, evidence_path, archives_directory)
    print(f"Validated Supabase Unity {version}: {len(files)} release files passed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
