#!/usr/bin/env python3
"""Build GitHub release notes from the matching package changelog entry."""

from __future__ import annotations

import argparse
import json
import os
import re
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
PACKAGE = ROOT / "Packages" / "com.supabaseunity.client"
PACKAGE_NAME = "com.supabaseunity.client"
DEFAULT_VERIFICATION = ROOT / ".github" / "release-verification.json"
CHANGELOG_HEADING = re.compile(r"^## \[([^]]+)](?:\s+-\s+.+)?$")


def package_version() -> str:
    manifest = json.loads((PACKAGE / "package.json").read_text(encoding="utf-8-sig"))
    return str(manifest["version"])


def changelog_entry(version: str) -> str:
    lines = (PACKAGE / "CHANGELOG.md").read_text(encoding="utf-8-sig").splitlines()
    start: int | None = None
    end = len(lines)

    for index, line in enumerate(lines):
        match = CHANGELOG_HEADING.match(line)
        if not match:
            continue
        if start is None and match.group(1) == version:
            start = index + 1
            continue
        if start is not None:
            end = index
            break

    if start is None:
        raise ValueError(f"CHANGELOG.md has no entry for {version}")

    entry = "\n".join(lines[start:end]).strip()
    if not entry:
        raise ValueError(f"CHANGELOG.md entry for {version} is empty")
    return entry


def read_verification(path: Path, version: str) -> dict[str, object] | None:
    if not path.is_file():
        return None
    record = json.loads(path.read_text(encoding="utf-8-sig"))
    if record.get("packageVersion") != version:
        return None
    return record


def build_notes(
    version: str,
    repository: str,
    verification: dict[str, object] | None = None,
) -> str:
    tag = f"v{version}"
    release_root = f"https://github.com/{repository}/releases/download/{tag}"
    unitypackage = f"{PACKAGE_NAME}-{version}.unitypackage"
    tarball = f"{PACKAGE_NAME}-{version}.tgz"
    changelog_url = (
        f"https://github.com/{repository}/blob/{tag}/"
        f"Packages/{PACKAGE_NAME}/CHANGELOG.md"
    )

    verification_download = ""
    verification_section = ""
    if verification is not None:
        unity = verification["unity"]
        assert isinstance(unity, dict)
        tests = unity["editModeTests"]
        assert isinstance(tests, dict)
        player_builds = unity.get("playerBuilds")
        if isinstance(player_builds, dict):
            player_build_summary = (
                "- WebGL, Windows, and Android smoke players built successfully.\n"
                "- Unity generated the iOS Xcode project successfully."
            )
        else:
            player_build_summary = "- The WebGL smoke player built successfully."
        verification_download = (
            "| Verification record | Local Unity release-gate results | "
            f"[Download JSON]({release_root}/release-verification.json) |\n"
        )
        verification_section = f"""
## Verification

This release passed the local Unity release gate with **Unity {unity['editorVersion']}**:

- **{tests['passed']}/{tests['total']} EditMode tests** passed.
- The generated `.unitypackage` compiled after import into a clean Unity project.
{player_build_summary}

The hosted release job rebuilt both archives and matched them against the attached
[verification record]({release_root}/release-verification.json) before publishing.
"""

    return f"""## Downloads

| Format | Installation | Download |
| --- | --- | --- |
| Unity package | **Assets > Import Package > Custom Package** | [Download `.unitypackage`]({release_root}/{unitypackage}) |
| UPM tarball | **Package Manager > Add package from tarball** | [Download `.tgz`]({release_root}/{tarball}) |
| Checksums | Verify downloaded files | [Download `SHA256SUMS`]({release_root}/SHA256SUMS) |
{verification_download.rstrip()}

The `.unitypackage` imports to `Assets/SupabaseUnity` and includes the package README,
changelog, and license. Install `com.unity.nuget.newtonsoft-json` 3.2.1 or newer first. Do
not install the `.unitypackage` alongside the UPM distribution; both contain the same
assemblies.
{verification_section}

## Changelog

{changelog_entry(version)}

See the [complete package changelog]({changelog_url}).
"""


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", default=package_version())
    parser.add_argument(
        "--repository",
        default=os.environ.get("GITHUB_REPOSITORY", "DorukTan/unity-supabase"),
    )
    parser.add_argument("--verification", type=Path, default=DEFAULT_VERIFICATION)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()

    notes = build_notes(
        args.version,
        args.repository,
        read_verification(args.verification, args.version),
    )
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(notes, encoding="utf-8", newline="\n")
    print(f"Wrote release notes for {args.version} to {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
