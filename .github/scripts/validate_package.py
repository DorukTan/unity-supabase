#!/usr/bin/env python3
"""License-free integrity and credential checks for the repository."""

from __future__ import annotations

import json
import re
import subprocess
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
PACKAGE = ROOT / "Packages" / "com.supabaseunity.client"
VERSION_PATTERN = re.compile(r"^\d+\.\d+\.\d+(?:-(?:alpha|beta|rc)\.\d+)?$")
GUID_PATTERN = re.compile(r"(?m)^guid: ([0-9a-f]{32})$")
PINNED_ACTION_PATTERN = re.compile(r"^[^@]+@[0-9a-f]{40}$")
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"

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
        ROOT / "LICENSE.md",
        ROOT / "SECURITY.md",
        ROOT / ".gitattributes",
        ROOT / ".gitignore",
    ]
    files: list[Path] = []
    for root in roots:
        if root.is_file():
            files.append(root)
        elif root.is_dir():
            files.extend(path for path in root.rglob("*") if path.is_file())
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


def validate_workflows() -> None:
    workflow_dir = ROOT / ".github" / "workflows"
    for workflow in workflow_dir.glob("*.yml"):
        text = read_text(workflow)
        if re.search(r"(?m)^\s*pull_request_target\s*:", text):
            fail(f"Unsafe pull_request_target trigger in {workflow.relative_to(ROOT)}")
        for match in re.finditer(r"(?m)^\s*(?:-\s*)?uses:\s*([^\s#]+)", text):
            action = match.group(1)
            if action.startswith("./"):
                continue
            if not PINNED_ACTION_PATTERN.fullmatch(action):
                fail(f"GitHub Action is not pinned by commit SHA: {action}")

    unity_workflow = read_text(workflow_dir / "unity-package-tests.yml")
    if "workflow_dispatch:" not in unity_workflow:
        fail("Licensed Unity tests must remain manually dispatchable")


def main() -> int:
    files = release_files()
    version = validate_package_manifest()
    validate_client_info(version)
    validate_json_files(files)
    validate_credentials_and_text(files)
    validate_credentials_and_text(tracked_files(), check_trailing_whitespace=False)
    validate_unity_meta()
    validate_workflows()
    print(f"Validated Supabase Unity {version}: {len(files)} release files passed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
