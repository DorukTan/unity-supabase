# Contributing

Bug reports and focused pull requests are welcome. Please read this first; it is short and
it will save you a round trip.

## Before you start

Open an issue before writing a large change. A pull request that adds a feature nobody asked
for, or that restructures code for reasons the maintainer does not share, is likely to be
declined no matter how good it is. That is a waste of your time, and avoiding it is the
point of this paragraph.

Small fixes, documentation corrections, and platform reports need no prior discussion.

## Running the tests

Open the project in Unity and use **Window > General > Test Runner > EditMode > Run All**, or
from the command line:

```bash
"C:/Program Files/Unity/Hub/Editor/6000.4.8f1/Editor/Unity.exe" -runTests -batchmode -projectPath . -testPlatform EditMode -testResults TestResults.xml -logFile -
```

Use the editor version in `ProjectSettings/ProjectVersion.txt`. Unity holds an exclusive lock
on the project directory, so close the Editor before running batchmode. Read `TestResults.xml`
for the real result; batchmode stdout is not a reliable pass/fail signal. Do not commit
`TestResults.xml`.

## The pre-push hook

Hosted checks intentionally remain license-free, so a hook runs Unity locally on the machine
where it is already activated through Unity Hub. Enable it once:

```bash
git config core.hooksPath .githooks
```

`core.hooksPath` is local git config and does not travel with a clone, so every contributor
runs that line themselves.

What it does on `git push`:

- **Every push:** runs `validate_package.py`. Takes about a second. Blocks on failure.
- **Pushing `main` or a tag:** also runs the full EditMode suite and blocks if anything
  fails. Takes a few minutes.
- **Pushing a tag:** also rebuilds the release archives and checks them against the committed
  local verification record.

Feature branches skip the Unity run by default, since what matters is what gets released.
Force it anywhere with `RUN_TESTS=1 git push`.

If the hook cannot find your editor, set `UNITY_PATH=/path/to/Unity`. It reads the expected
version from `ProjectSettings/ProjectVersion.txt` and looks in the usual Unity Hub
locations. It refuses to run while the Unity Editor has the project open, because Unity
holds an exclusive lock and batchmode would fail.

`git push --no-verify` bypasses it. There is no CI safety net behind that, so it should be
rare and deliberate.

## Before you open the pull request

Run the package validator. It gates CI and catches most mechanical mistakes:

```bash
python .github/scripts/validate_package.py
```

It enforces, among other things:

- **No trailing whitespace** in any released file.
- **Committed `.meta` files** for everything new under `Packages/`. Unity generates them when
  the Editor opens the project or when batchmode runs; commit the file and its `.meta`
  together.
- **Action pins by full commit SHA** in workflows. Tag references are rejected. Resolve a real
  SHA with `gh api repos/OWNER/REPO/git/refs/tags` rather than guessing.
- **No credentials anywhere**, including inside binary assets.
- **Version consistency** between `package.json` and `SupabaseHttp.ClientInfo`.

## Code conventions

- The runtime targets the **Unity 2021.3 API surface**. Do not use newer APIs in
  `Runtime/`, and do not use editor-only APIs there at all.
- Match the surrounding C# style. This codebase uses `delegate { }` rather than lambdas and
  `default(CancellationToken)` rather than `default`, for older-compiler compatibility. Please
  do not modernize it in passing.
- Network operations return `SupabaseResult` or `SupabaseResult<T>`. Expected failures are
  values, not exceptions. Keep it that way.
- New behavior needs a test. `Tests/Runtime/` uses fake transports; see `TestDoubles.cs`.

## What hosted checks will and will not run on your PR

**GitHub Actions does not compile C# or run Unity tests.** Unity Personal activation is handled
through Unity Hub and is not available to the hosted runner. The repository therefore has no
placeholder Unity workflow and no green check that merely means Unity was skipped.

What does run is explicitly named **License-free package checks**: package structure,
credential scanning, action pinning, metadata and whitespace checks, version consistency,
deterministic archive construction, release-note generation, and WebGL plugin syntax. These
checks are useful, but they will not catch a C# compile error or a broken Unity test.

**This means running the EditMode suite locally is not optional.** A green checkmark on your
pull request does not mean your code builds. Please run the tests and say so in the pull
request description, including the pass count you saw.

Maintainers preparing a tag must run the complete local gate documented in
[RELEASING.md](RELEASING.md). It additionally performs a clean `.unitypackage` import, a WebGL
build, and produces the archive hashes enforced by the tag workflow.

## Things to keep out of pull requests

Generated files, `Library/`, Unity project-specific settings, your `SupabaseSettings.asset`,
and anything containing a credential. If you think you have committed a key, rotate it in
Supabase first and mention it in the pull request second.
