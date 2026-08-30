#!/usr/bin/env python3
"""Build deterministic UPM and unitypackage release archives."""

from __future__ import annotations

import argparse
import gzip
import hashlib
import json
import os
import re
import tarfile
from dataclasses import dataclass
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
PACKAGE = ROOT / "Packages" / "com.supabaseunity.client"
PACKAGE_NAME = "com.supabaseunity.client"
UNITY_TARGET_ROOT = Path("Assets") / "SupabaseUnity"
GUID_PATTERN = re.compile(r"(?m)^guid: ([0-9a-f]{32})$")
ARCHIVE_MTIME = int(os.environ.get("SOURCE_DATE_EPOCH", "0"))
TEXT_SUFFIXES = {
    ".asmdef",
    ".cs",
    ".jslib",
    ".json",
    ".md",
    ".meta",
    ".sql",
    ".txt",
    ".xml",
}


@dataclass(frozen=True)
class UnityAsset:
    source: Path | None
    meta: bytes
    pathname: str
    guid: str


def read_source_bytes(path: Path) -> bytes:
    """Read package text with platform-independent line endings."""
    data = path.read_bytes()
    if path.suffix.lower() in TEXT_SUFFIXES:
        return data.replace(b"\r\n", b"\n").replace(b"\r", b"\n")
    return data


def read_manifest() -> dict[str, object]:
    return json.loads((PACKAGE / "package.json").read_text(encoding="utf-8-sig"))


def tar_info(name: str, size: int = 0, *, directory: bool = False) -> tarfile.TarInfo:
    info = tarfile.TarInfo(name)
    info.mtime = ARCHIVE_MTIME
    info.uid = 0
    info.gid = 0
    info.uname = ""
    info.gname = ""
    info.mode = 0o755 if directory else 0o644
    if directory:
        info.type = tarfile.DIRTYPE
    else:
        info.size = size
    return info


def add_bytes(archive: tarfile.TarFile, name: str, data: bytes) -> None:
    import io

    archive.addfile(tar_info(name, len(data)), io.BytesIO(data))


def write_gzip_tar(path: Path, write_entries) -> None:
    with path.open("wb") as raw:
        with gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=ARCHIVE_MTIME) as zipped:
            with tarfile.open(fileobj=zipped, mode="w", format=tarfile.USTAR_FORMAT) as archive:
                write_entries(archive)


def build_upm_archive(path: Path) -> None:
    def write_entries(archive: tarfile.TarFile) -> None:
        archive.addfile(tar_info("package/", directory=True))
        for source in sorted(PACKAGE.rglob("*"), key=lambda item: item.as_posix()):
            if source.is_symlink():
                raise ValueError(f"Symlinks are not supported: {source.relative_to(ROOT)}")
            relative = source.relative_to(PACKAGE).as_posix()
            archive_name = f"package/{relative}"
            if source.is_dir():
                archive.addfile(tar_info(f"{archive_name}/", directory=True))
            elif source.is_file():
                add_bytes(archive, archive_name, read_source_bytes(source))

    write_gzip_tar(path, write_entries)


def read_guid(meta_path: Path) -> str:
    text = meta_path.read_text(encoding="utf-8-sig")
    match = GUID_PATTERN.search(text)
    if not match:
        raise ValueError(f"No Unity GUID in {meta_path.relative_to(ROOT)}")
    return match.group(1)


def generated_folder_meta(guid: str) -> bytes:
    return (
        "fileFormatVersion: 2\n"
        f"guid: {guid}\n"
        "folderAsset: yes\n"
        "DefaultImporter:\n"
        "  externalObjects: {}\n"
        "  userData: \n"
        "  assetBundleName: \n"
        "  assetBundleVariant: \n"
    ).encode("utf-8")


def generated_file_meta(guid: str, suffix: str) -> bytes:
    if suffix.lower() == ".cs":
        importer = (
            "MonoImporter:\n"
            "  externalObjects: {}\n"
            "  serializedVersion: 2\n"
            "  defaultReferences: []\n"
            "  executionOrder: 0\n"
            "  icon: {instanceID: 0}\n"
        )
    elif suffix.lower() == ".md":
        importer = "TextScriptImporter:\n  externalObjects: {}\n"
    else:
        importer = "DefaultImporter:\n  externalObjects: {}\n"
    return (
        "fileFormatVersion: 2\n"
        f"guid: {guid}\n"
        f"{importer}"
        "  userData: \n"
        "  assetBundleName: \n"
        "  assetBundleVariant: \n"
    ).encode("utf-8")


def generated_guid(pathname: str) -> str:
    return hashlib.sha256(
        f"{PACKAGE_NAME}:{pathname}:v1".encode("utf-8")
    ).hexdigest()[:32]


def read_unity_asset_bytes(path: Path) -> bytes:
    data = read_source_bytes(path)
    if path.suffix.lower() == ".md":
        text = data.decode("utf-8-sig")
        text = re.sub(r"(?<!/)Documentation~/", "Documentation/", text)
        text = re.sub(r"(?<!/)Samples~/", "Samples/", text)
        text = text.replace("../Documentation~/", "../Documentation/")
        text = text.replace("../Samples~/", "../Samples/")
        data = text.encode("utf-8")
    return data


def unity_assets() -> list[UnityAsset]:
    root_guid = hashlib.sha256(
        b"com.supabaseunity.client:Assets/SupabaseUnity:v1"
    ).hexdigest()[:32]
    assets = [
        UnityAsset(None, generated_folder_meta(root_guid), UNITY_TARGET_ROOT.as_posix(), root_guid)
    ]

    for directory_name in ("Runtime", "Editor"):
        source_root = PACKAGE / directory_name
        candidates = [source_root]
        candidates.extend(sorted(source_root.rglob("*"), key=lambda item: item.as_posix()))
        for source in candidates:
            if source.name.endswith(".meta"):
                continue
            if source.is_symlink():
                raise ValueError(f"Symlinks are not supported: {source.relative_to(ROOT)}")
            meta_path = Path(f"{source}.meta")
            if not meta_path.is_file():
                raise ValueError(f"Missing Unity meta file: {meta_path.relative_to(ROOT)}")
            target = UNITY_TARGET_ROOT / source.relative_to(PACKAGE)
            assets.append(
                UnityAsset(
                    source if source.is_file() else None,
                    read_source_bytes(meta_path),
                    target.as_posix(),
                    read_guid(meta_path),
                )
            )

    for source_name, target_name in (
        ("Documentation~", "Documentation"),
        ("Samples~", "Samples"),
    ):
        source_root = PACKAGE / source_name
        target_root = UNITY_TARGET_ROOT / target_name
        candidates = [source_root]
        candidates.extend(sorted(source_root.rglob("*"), key=lambda item: item.as_posix()))
        for source in candidates:
            if source.name.endswith(".meta"):
                continue
            if source.is_symlink():
                raise ValueError(f"Symlinks are not supported: {source.relative_to(ROOT)}")
            target = target_root / source.relative_to(source_root)
            pathname = target.as_posix()
            guid = generated_guid(pathname)
            assets.append(
                UnityAsset(
                    source if source.is_file() else None,
                    generated_file_meta(guid, source.suffix)
                    if source.is_file()
                    else generated_folder_meta(guid),
                    pathname,
                    guid,
                )
            )

    for filename in ("README.md", "README.tr.md", "CHANGELOG.md", "LICENSE.md"):
        source = PACKAGE / filename
        meta_path = Path(f"{source}.meta")
        assets.append(
            UnityAsset(
                source,
                read_source_bytes(meta_path),
                (UNITY_TARGET_ROOT / filename).as_posix(),
                read_guid(meta_path),
            )
        )

    assets.sort(key=lambda item: item.pathname)
    guids = [asset.guid for asset in assets]
    if len(guids) != len(set(guids)):
        raise ValueError("The unitypackage contains duplicate Unity GUIDs")
    return assets


def build_unitypackage(path: Path) -> None:
    assets = unity_assets()

    def write_entries(archive: tarfile.TarFile) -> None:
        for item in assets:
            archive.addfile(tar_info(f"{item.guid}/", directory=True))
            if item.source is not None:
                add_bytes(archive, f"{item.guid}/asset", read_unity_asset_bytes(item.source))
            add_bytes(archive, f"{item.guid}/asset.meta", item.meta)
            add_bytes(archive, f"{item.guid}/pathname", item.pathname.encode("utf-8"))

    write_gzip_tar(path, write_entries)


def validate_upm_archive(path: Path, version: str) -> None:
    with tarfile.open(path, "r:gz") as archive:
        members = archive.getmembers()
        if any(
            member.name != "package" and not member.name.startswith("package/")
            for member in members
        ):
            raise ValueError("UPM archive contains a path outside package/")
        manifest = archive.extractfile("package/package.json")
        if manifest is None:
            raise ValueError("UPM archive has no package.json")
        archived = json.loads(manifest.read().decode("utf-8-sig"))
        if archived.get("name") != PACKAGE_NAME or archived.get("version") != version:
            raise ValueError("UPM archive manifest does not match the release version")


def validate_unitypackage(path: Path) -> None:
    with tarfile.open(path, "r:gz") as archive:
        files = {member.name: member for member in archive.getmembers() if member.isfile()}
        pathnames = [name for name in files if name.endswith("/pathname")]
        if not pathnames:
            raise ValueError("unitypackage contains no assets")
        seen_paths: set[str] = set()
        for pathname_member in pathnames:
            guid = pathname_member.split("/", 1)[0]
            meta_member = f"{guid}/asset.meta"
            if meta_member not in files:
                raise ValueError(f"unitypackage asset {guid} has no meta file")
            extracted = archive.extractfile(files[pathname_member])
            if extracted is None:
                raise ValueError(f"Cannot read {pathname_member}")
            pathname = extracted.read().decode("utf-8")
            if not pathname.startswith(f"{UNITY_TARGET_ROOT.as_posix()}/") and pathname != UNITY_TARGET_ROOT.as_posix():
                raise ValueError(f"unitypackage path is outside {UNITY_TARGET_ROOT}: {pathname}")
            if pathname in seen_paths:
                raise ValueError(f"Duplicate unitypackage path: {pathname}")
            seen_paths.add(pathname)

        forbidden = ("/Tests/", "/Samples~/", "/Documentation~/")
        if any(token in pathname for pathname in seen_paths for token in forbidden):
            raise ValueError("unitypackage includes development-only package content")

        required = {
            f"{UNITY_TARGET_ROOT.as_posix()}/Documentation/getting-started.md",
            f"{UNITY_TARGET_ROOT.as_posix()}/Documentation/troubleshooting.md",
            f"{UNITY_TARGET_ROOT.as_posix()}/Samples/Quickstart/README.md",
            f"{UNITY_TARGET_ROOT.as_posix()}/Samples/Quickstart/setup.sql",
            f"{UNITY_TARGET_ROOT.as_posix()}/Samples/Quickstart/SupabaseQuickstart.cs",
        }
        missing = sorted(required - seen_paths)
        if missing:
            raise ValueError(
                "unitypackage is missing documentation or Quickstart assets: "
                + ", ".join(missing)
            )


def write_checksums(path: Path, archives: list[Path]) -> None:
    lines = []
    for archive in sorted(archives, key=lambda item: item.name):
        digest = hashlib.sha256(archive.read_bytes()).hexdigest()
        lines.append(f"{digest}  {archive.name}")
    path.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, default=ROOT / "dist")
    args = parser.parse_args()

    manifest = read_manifest()
    version = str(manifest["version"])
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)

    upm_path = output / f"{PACKAGE_NAME}-{version}.tgz"
    unitypackage_path = output / f"{PACKAGE_NAME}-{version}.unitypackage"
    checksum_path = output / "SHA256SUMS"

    build_upm_archive(upm_path)
    build_unitypackage(unitypackage_path)
    validate_upm_archive(upm_path, version)
    validate_unitypackage(unitypackage_path)
    write_checksums(checksum_path, [upm_path, unitypackage_path])

    print(f"Built {upm_path}")
    print(f"Built {unitypackage_path}")
    print(f"Wrote {checksum_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
