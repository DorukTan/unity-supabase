# Releasing

GitHub-hosted workflows intentionally remain license-free. They validate package structure,
credentials, deterministic archives, release notes, and the evidence recorded by a locally
activated Unity Editor. They do not compile C# or present a skipped Unity job as a successful
test run.

## Prerequisites

- The Unity version in `ProjectSettings/ProjectVersion.txt`, activated through Unity Hub.
- WebGL, Windows, Android, and iOS Build Support modules for that editor version. The iOS
  check generates an Xcode project; producing and running the final app still requires macOS,
  Xcode, signing, and hardware.
- Python 3.
- The project closed in Unity while the command-line checks run.

No Unity serial, license file, email, or password belongs in this repository or its GitHub
secrets.

## Prepare a release

1. Update `package.json`, `SupabaseHttp.ClientInfo`, installation URLs, and the changelog to the
   same version.
2. Commit the release candidate so the source being verified is unambiguous.
3. Close the Unity Editor and run the complete local gate from the repository root:

   ```bash
   python .github/scripts/verify_release.py
   ```

4. Inspect `.github/release-verification.json` and the generated files in `dist/`.
5. Commit `.github/release-verification.json`.
6. Create and push the matching `v<version>` tag.

Use `UNITY_PATH=/path/to/Unity` or `--unity /path/to/Unity` if the editor is not installed in
Unity Hub's default location.

## What the local gate proves

The command stops on the first failed boundary and only writes a successful verification
record after all of these checks pass:

- Package structure, version consistency, credential scanning, Unity metadata, and workflow
  policy.
- Deterministic `.tgz` and `.unitypackage` creation.
- The complete EditMode test suite in the project's configured Unity version.
- Import and compilation of the generated `.unitypackage` in a newly created temporary Unity
  project with its Newtonsoft Json.NET dependency installed.
- WebGL, Windows, and Android player builds from a minimal scene in that clean temporary
  project, with a component referencing the imported runtime assembly.
- iOS Xcode project generation from the same imported package and probe scene. This verifies
  Unity compilation and export, not Xcode compilation, signing, or a hardware run.
- SHA-256 matching between the tested local archives and the archives rebuilt by the hosted
  release workflow.

The JSON record contains only the package version, UTC verification time, Unity version, test
counts, boundary results, and archive hashes. Local paths, usernames, logs, credentials, and
Unity account details are never included.

## Publishing behavior

The tag workflow independently rebuilds both archives. Publishing is rejected if the tag does
not match `package.json`, the verification record is missing or stale, or either archive hash
differs from the locally tested artifact. A successful release contains:

- `com.supabaseunity.client-<version>.unitypackage`
- `com.supabaseunity.client-<version>.tgz`
- `SHA256SUMS`
- `release-verification.json`
- Release notes generated from the matching changelog entry

Do not recreate or move an existing version tag. Correct the problem, increment the prerelease
or patch version, rerun the gate, and publish a new tag.
