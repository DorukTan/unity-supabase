#!/usr/bin/env python3
"""Run the authoritative, locally licensed Unity release gate."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as element_tree
from datetime import datetime, timezone
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
PACKAGE = ROOT / "Packages" / "com.supabaseunity.client"
PROJECT_VERSION = ROOT / "ProjectSettings" / "ProjectVersion.txt"
DEFAULT_EVIDENCE = ROOT / ".github" / "release-verification.json"
WEBGL_SUCCESS_MARKER = "SUPABASE_RELEASE_WEBGL_OK"
CLEAN_IMPORT_SUCCESS_MARKER = "SUPABASE_RELEASE_CLEAN_IMPORT_OK"

CLEAN_IMPORT_VERIFIER = r"""using System;
using System.IO;
using Supabase.Unity;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class SupabaseUnityCleanImportVerifier
{
    public static void Verify()
    {
        if (!File.Exists("Assets/SupabaseUnity/Runtime/SupabaseClient.cs"))
            throw new InvalidOperationException("The unitypackage did not import its runtime files.");

        if (typeof(SupabaseClient).Assembly.GetName().Name != "Supabase.Unity.Runtime")
            throw new InvalidOperationException("The imported runtime assembly is unavailable.");

        Debug.Log("SUPABASE_RELEASE_CLEAN_IMPORT_OK");
    }

    public static void BuildWebGL()
    {
        var outputPath = Environment.GetEnvironmentVariable("SUPABASE_UNITY_WEBGL_OUTPUT");
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new InvalidOperationException("SUPABASE_UNITY_WEBGL_OUTPUT is not set.");

        var scenePath = "Assets/SupabaseUnityReleaseVerification.unity";
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var gameObject = new GameObject("Supabase Unity Build Probe");
        gameObject.AddComponent<SupabaseUnityBuildProbe>();
        if (!EditorSceneManager.SaveScene(scene, scenePath))
            throw new InvalidOperationException("Could not save the WebGL verification scene.");

        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.WebGL &&
            !EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.WebGL, BuildTarget.WebGL))
        {
            throw new InvalidOperationException("Unity could not switch to the WebGL build target.");
        }

        outputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(outputPath);
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { scenePath },
            locationPathName = outputPath,
            target = BuildTarget.WebGL,
            options = BuildOptions.None
        });
        if (report.summary.result != BuildResult.Succeeded)
        {
            throw new InvalidOperationException(
                "WebGL build failed with result " + report.summary.result +
                " and " + report.summary.totalErrors + " errors.");
        }

        Debug.Log(
            "SUPABASE_RELEASE_WEBGL_OK bytes=" + report.summary.totalSize +
            " output=" + report.summary.outputPath);
    }
}
"""

CLEAN_IMPORT_BUILD_PROBE = r"""using Supabase.Unity;
using UnityEngine;

public sealed class SupabaseUnityBuildProbe : MonoBehaviour
{
    public string ClientAssemblyName
    {
        get { return typeof(SupabaseClient).Assembly.GetName().Name; }
    }
}
"""


class VerificationError(RuntimeError):
    pass


def project_unity_version() -> str:
    text = PROJECT_VERSION.read_text(encoding="utf-8-sig")
    match = re.search(r"(?m)^m_EditorVersion:\s*(\S+)\s*$", text)
    if not match:
        raise VerificationError(f"Could not read the Unity version from {PROJECT_VERSION}")
    return match.group(1)


def package_version() -> str:
    manifest = json.loads((PACKAGE / "package.json").read_text(encoding="utf-8-sig"))
    return str(manifest["version"])


def ensure_project_is_unlocked() -> None:
    lockfile = ROOT / "Temp" / "UnityLockfile"
    if not lockfile.exists():
        return

    if os.name == "nt":
        try:
            lockfile.unlink()
        except OSError as exception:
            raise VerificationError(
                "This project is open in Unity. Close the Editor before verification."
            ) from exception
        print("Removed a stale Temp/UnityLockfile left by an earlier Unity process.")
        return

    lock_owners: subprocess.CompletedProcess[str] | None = None
    if shutil.which("lsof"):
        lock_owners = subprocess.run(
            ["lsof", str(lockfile)], capture_output=True, text=True, check=False
        )
    elif shutil.which("fuser"):
        lock_owners = subprocess.run(
            ["fuser", str(lockfile)], capture_output=True, text=True, check=False
        )
    if lock_owners is None:
        raise VerificationError(
            "Temp/UnityLockfile exists and neither lsof nor fuser is available to prove it "
            "is stale. Close Unity, remove the stale lockfile, and retry."
        )
    if lock_owners.returncode == 0:
        raise VerificationError(
            "This project is open in Unity. Close the Editor before verification."
        )
    lockfile.unlink()
    print("Removed a stale Temp/UnityLockfile left by an earlier Unity process.")


def find_unity(explicit_path: str | None, version: str) -> Path:
    configured = explicit_path or os.environ.get("UNITY_PATH")
    candidates: list[Path] = []
    if configured:
        candidates.append(Path(configured).expanduser())

    if sys.platform == "win32":
        candidates.extend(
            [
                Path(f"C:/Program Files/Unity/Hub/Editor/{version}/Editor/Unity.exe"),
                Path("C:/Program Files/Unity/Editor/Unity.exe"),
            ]
        )
    elif sys.platform == "darwin":
        candidates.extend(
            [
                Path(f"/Applications/Unity/Hub/Editor/{version}/Unity.app/Contents/MacOS/Unity"),
                Path("/Applications/Unity/Unity.app/Contents/MacOS/Unity"),
            ]
        )
    else:
        candidates.extend(
            [
                Path.home() / f"Unity/Hub/Editor/{version}/Editor/Unity",
                Path(f"/opt/unity/editors/{version}/Editor/Unity"),
            ]
        )

    for candidate in candidates:
        if candidate.is_file():
            return candidate.resolve()

    locations = "\n".join(f"  - {candidate}" for candidate in candidates)
    raise VerificationError(
        f"Unity {version} was not found. Set UNITY_PATH or pass --unity. Checked:\n{locations}"
    )


def tail(path: Path, line_count: int = 60) -> str:
    if not path.is_file():
        return "(log file was not created)"
    lines = path.read_text(encoding="utf-8-sig", errors="replace").splitlines()
    return "\n".join(lines[-line_count:])


def run_command(command: list[str], label: str, cwd: Path = ROOT) -> None:
    print(f"\n==> {label}", flush=True)
    completed = subprocess.run(command, cwd=cwd, check=False)
    if completed.returncode != 0:
        raise VerificationError(f"{label} failed with exit code {completed.returncode}")


def run_unity(
    unity: Path,
    arguments: list[str],
    log_path: Path,
    label: str,
    *,
    environment: dict[str, str] | None = None,
    required_marker: str | None = None,
    trust_exit_code: bool = True,
) -> int:
    print(f"\n==> {label}", flush=True)
    command = [str(unity), *arguments, "-logFile", str(log_path)]
    completed = subprocess.run(
        command,
        cwd=ROOT,
        env=environment,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.STDOUT,
        check=False,
    )
    log_text = (
        log_path.read_text(encoding="utf-8-sig", errors="replace")
        if log_path.is_file()
        else ""
    )
    if trust_exit_code and completed.returncode != 0:
        raise VerificationError(
            f"{label} failed with exit code {completed.returncode}.\n\n{tail(log_path)}"
        )
    if re.search(r"\berror CS\d{4}\b", log_text):
        raise VerificationError(f"{label} produced C# compiler errors.\n\n{tail(log_path)}")
    if required_marker and required_marker not in log_text:
        raise VerificationError(
            f"{label} did not produce its success marker.\n\n{tail(log_path)}"
        )
    return completed.returncode


def parse_test_results(results_path: Path, log_path: Path) -> dict[str, int]:
    if not results_path.is_file() or results_path.stat().st_size == 0:
        raise VerificationError(
            "Unity produced no EditMode test results. This usually means compilation failed."
            f"\n\n{tail(log_path)}"
        )
    try:
        root = element_tree.parse(results_path).getroot()
    except element_tree.ParseError as exception:
        raise VerificationError(f"Unity produced invalid test XML: {exception}") from exception

    if root.tag != "test-run":
        root = root.find(".//test-run")
    if root is None:
        raise VerificationError("Unity test XML contains no <test-run> element")

    def integer_attribute(name: str) -> int:
        value = root.attrib.get(name, "0")
        return int(value) if value.isdigit() else 0

    result = {
        "total": integer_attribute("total"),
        "passed": integer_attribute("passed"),
        "failed": integer_attribute("failed"),
        "skipped": integer_attribute("skipped"),
        "inconclusive": integer_attribute("inconclusive"),
    }
    if result["total"] == 0:
        raise VerificationError("Unity discovered zero EditMode tests")
    if result["failed"] != 0 or result["passed"] != result["total"]:
        failed_names = sorted(
            {
                case.attrib.get("fullname") or case.attrib.get("name") or "unknown test"
                for case in root.findall(".//test-case")
                if case.attrib.get("result") == "Failed"
            }
        )
        details = ", ".join(failed_names[:8]) or "see the Unity test log"
        raise VerificationError(
            f"EditMode tests did not all pass: {result['passed']}/{result['total']} passed; "
            f"{details}"
        )
    return result


def run_editmode_tests(unity: Path, temporary: Path) -> dict[str, int]:
    results = temporary / "editmode-results.xml"
    log = temporary / "editmode.log"
    run_unity(
        unity,
        [
            "-runTests",
            "-batchmode",
            "-projectPath",
            str(ROOT),
            "-testPlatform",
            "EditMode",
            "-testResults",
            str(results),
        ],
        log,
        "Run EditMode tests",
        trust_exit_code=False,
    )
    return parse_test_results(results, log)


def add_clean_project_dependency(project: Path) -> None:
    manifest_path = project / "Packages" / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    dependencies = manifest.setdefault("dependencies", {})
    dependencies["com.unity.nuget.newtonsoft-json"] = "3.2.1"
    manifest_path.write_text(
        json.dumps(manifest, indent=2) + "\n", encoding="utf-8", newline="\n"
    )


def verify_clean_unitypackage_import(
    unity: Path, unitypackage: Path, temporary: Path
) -> Path:
    project = temporary / "clean-import-project"
    create_log = temporary / "clean-import-create.log"
    run_unity(
        unity,
        ["-batchmode", "-quit", "-createProject", str(project)],
        create_log,
        "Create clean Unity project",
    )
    add_clean_project_dependency(project)

    import_log = temporary / "clean-import-package.log"
    run_unity(
        unity,
        [
            "-batchmode",
            "-quit",
            "-projectPath",
            str(project),
            "-importPackage",
            str(unitypackage),
        ],
        import_log,
        "Import generated .unitypackage into clean project",
    )

    imported_runtime = project / "Assets" / "SupabaseUnity" / "Runtime" / "SupabaseClient.cs"
    if not imported_runtime.is_file():
        raise VerificationError("The generated .unitypackage did not import its runtime files")

    verifier_directory = project / "Assets" / "Editor"
    verifier_directory.mkdir(parents=True, exist_ok=True)
    (verifier_directory / "SupabaseUnityCleanImportVerifier.cs").write_text(
        CLEAN_IMPORT_VERIFIER, encoding="utf-8", newline="\n"
    )
    (project / "Assets" / "SupabaseUnityBuildProbe.cs").write_text(
        CLEAN_IMPORT_BUILD_PROBE, encoding="utf-8", newline="\n"
    )

    verify_log = temporary / "clean-import-verify.log"
    run_unity(
        unity,
        [
            "-batchmode",
            "-quit",
            "-projectPath",
            str(project),
            "-executeMethod",
            "SupabaseUnityCleanImportVerifier.Verify",
        ],
        verify_log,
        "Compile and verify clean .unitypackage import",
        required_marker=CLEAN_IMPORT_SUCCESS_MARKER,
    )
    return project


def build_webgl(unity: Path, project: Path, temporary: Path) -> None:
    output = temporary / "webgl-build"
    log = temporary / "webgl-build.log"
    environment = os.environ.copy()
    environment["SUPABASE_UNITY_WEBGL_OUTPUT"] = str(output)
    run_unity(
        unity,
        [
            "-batchmode",
            "-quit",
            "-projectPath",
            str(project),
            "-buildTarget",
            "WebGL",
            "-executeMethod",
            "SupabaseUnityCleanImportVerifier.BuildWebGL",
        ],
        log,
        "Build WebGL smoke player",
        environment=environment,
        required_marker=WEBGL_SUCCESS_MARKER,
    )
    if not (output / "index.html").is_file():
        raise VerificationError("WebGL build reported success but produced no index.html")


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def write_evidence(
    path: Path,
    version: str,
    unity_version: str,
    tests: dict[str, int],
    archives: list[Path],
) -> None:
    evidence = {
        "schemaVersion": 1,
        "packageVersion": version,
        "verifiedAtUtc": datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace(
            "+00:00", "Z"
        ),
        "unity": {
            "editorVersion": unity_version,
            "editModeTests": tests,
            "cleanUnitypackageImport": "passed",
            "webglBuild": "passed",
        },
        "archives": {archive.name: sha256(archive) for archive in sorted(archives)},
    }
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(evidence, indent=2) + "\n", encoding="utf-8", newline="\n"
    )


def copy_release_output(source: Path, destination: Path, version: str) -> None:
    destination.mkdir(parents=True, exist_ok=True)
    names = [
        f"com.supabaseunity.client-{version}.tgz",
        f"com.supabaseunity.client-{version}.unitypackage",
        "SHA256SUMS",
    ]
    for name in names:
        shutil.copy2(source / name, destination / name)


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Run Unity tests, clean import, WebGL build, and release archive verification."
    )
    parser.add_argument("--unity", help="Path to the Unity executable")
    parser.add_argument("--output", type=Path, default=ROOT / "dist")
    parser.add_argument("--evidence", type=Path, default=DEFAULT_EVIDENCE)
    args = parser.parse_args()

    unity_version = project_unity_version()
    version = package_version()
    unity = find_unity(args.unity, unity_version)
    evidence_path = args.evidence.resolve()
    output = args.output.resolve()

    ensure_project_is_unlocked()

    print(f"Supabase for Unity {version} release verification")
    print(f"Unity: {unity} ({unity_version})")

    with tempfile.TemporaryDirectory(
        prefix="supabase-unity-release-", ignore_cleanup_errors=True
    ) as temporary_name:
        temporary = Path(temporary_name)
        archives = temporary / "archives"

        run_command(
            [sys.executable, str(ROOT / ".github/scripts/validate_package.py")],
            "Validate package and repository boundary",
        )
        run_command(
            [
                sys.executable,
                str(ROOT / ".github/scripts/build_release.py"),
                "--output",
                str(archives),
            ],
            "Build deterministic release archives",
        )

        tests = run_editmode_tests(unity, temporary)
        unitypackage = archives / f"com.supabaseunity.client-{version}.unitypackage"
        clean_project = verify_clean_unitypackage_import(unity, unitypackage, temporary)
        build_webgl(unity, clean_project, temporary)

        release_archives = [
            archives / f"com.supabaseunity.client-{version}.tgz",
            unitypackage,
        ]
        candidate_evidence = temporary / "release-verification.json"
        write_evidence(candidate_evidence, version, unity_version, tests, release_archives)
        run_command(
            [
                sys.executable,
                str(ROOT / ".github/scripts/validate_package.py"),
                "--release-archives",
                str(archives),
                "--release-verification",
                str(candidate_evidence),
            ],
            "Match release archives to local Unity verification",
        )

        candidate_notes = temporary / "RELEASE_NOTES.md"
        run_command(
            [
                sys.executable,
                str(ROOT / ".github/scripts/build_release_notes.py"),
                "--verification",
                str(candidate_evidence),
                "--output",
                str(candidate_notes),
            ],
            "Build release notes",
        )
        copy_release_output(archives, output, version)
        shutil.copy2(candidate_notes, output / "RELEASE_NOTES.md")
        evidence_path.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(candidate_evidence, evidence_path)

    print("\nRelease verification passed:")
    print(f"  EditMode: {tests['passed']}/{tests['total']} passed")
    print("  Clean .unitypackage import: passed")
    print("  WebGL smoke build: passed")
    print(f"  Archives: {output}")
    print(f"  Verification record: {evidence_path}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except VerificationError as exception:
        print(f"\nRelease verification failed: {exception}", file=sys.stderr)
        raise SystemExit(1)
