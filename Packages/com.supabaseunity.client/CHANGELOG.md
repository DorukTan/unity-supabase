# Changelog

## [Unreleased]

## [0.2.0-rc.1] - 2026-08-27

### Added

- A deterministic compatibility test now freezes the public runtime surface for `0.2.0`,
  covering 90 public types and their constructors, methods, properties, events, fields, enum
  values, generic constraints, optional defaults, and declared interfaces.

### Changed

- The package has entered release-candidate status. Changes between this release and `0.2.0`
  will be limited to compatible fixes, documentation corrections, and release validation.

## [0.2.0-beta.8] - 2026-08-25

### Added

- **Tools > Supabase > Setup** now creates or locates the settings asset, explains each client
  option, validates it locally, and tests the project URL and publishable key against Supabase
  without using or storing an elevated key.
- The Quickstart sample now includes repeatable SQL for its table, grants, Row Level Security
  policy, seed rows, and Realtime publication entry, plus complete steps from import to Play mode.
- A troubleshooting guide now maps common installation, configuration, policy, Realtime, OAuth,
  IL2CPP, and async failures to concrete checks.

### Changed

- The Setup window now groups configuration, connection testing, and Quickstart import into clear
  status-aware steps, with compact resource links and no duplicate validation menu.
- Installation guidance now covers the Package Manager menu in both supported LTS editors and
  Unity 6, while Setup Assistant links open documentation for the installed package version.
- The `.unitypackage` now includes the complete documentation and runnable Quickstart under
  `Assets/SupabaseUnity`, so both release formats ship the starter experience.
- Settings fields now explain where values come from and what each runtime option changes.
- The Quickstart reports missing settings and operation failures with actionable context, lists
  the loaded rows, and disposes synchronously when its GameObject is destroyed.

## [0.2.0-beta.7] - 2026-08-23

### Changed

- The local release gate now builds the cleanly imported `.unitypackage` for WebGL, Windows,
  and Android, then generates an iOS Xcode project from the same probe scene.
- Public release verification now records each platform build separately and rejects a release
  if any required target is missing or failed. The iOS result covers Unity compilation and
  project generation; Xcode compilation, signing, and hardware testing remain separate.

## [0.2.0-beta.6] - 2026-08-23

### Fixed

- `AuthSignOutScope.Others` now revokes other sessions without clearing the current local session.
- A token refresh that finishes after a newer sign-in or sign-out can no longer overwrite the
  newer Auth state.
- Canceling one `RefreshSessionAsync` caller no longer cancels the shared token refresh for every
  concurrent caller.
- Slower sign-in and sign-out responses can no longer overwrite a session operation that started
  later. Superseded session results fail with `auth_operation_superseded`.
- Profile fetch, profile update, and identity-unlink responses no longer modify `CurrentUser`
  after sign-out, an account switch, or a newer user operation. Token refreshes for the same user
  remain compatible.
- Session-store operations are now ordered so a slower write, removal, or startup read cannot
  leave durable Auth state older than `CurrentSession`.
- Once Auth adopts a session change, its session-store commit now finishes even if the caller
  cancels, preventing a restart from restoring the state that preceded a completed sign-in or
  sign-out.
- Client-generated Auth failures now use stable error codes, and OAuth callback failures preserve
  the standard OAuth `error` code when an `error_description` is also present.
- Auth state-change notifications now follow accepted session order and no longer emit a stale
  `SignedOut` or `TokenRefreshed` event after a newer session operation takes ownership.

## [0.2.0-beta.5] - 2026-08-21

### Fixed

- Exceptions thrown by custom HTTP transports now become retryable `Transport` results across
  Auth, Database, Storage, and Edge Functions. `TimeoutException` becomes a `Timeout` result,
  while caller cancellation continues to throw `OperationCanceledException`.
- Realtime now negotiates Phoenix protocol 2.0 to match its five-element array wire format.

### Added

- Loopback coverage for Auth, PostgREST, Storage, and Edge Functions error responses, including
  retry classification and credential redaction.
- Regressions for repeated Realtime reconnects and disposal with an outstanding acknowledgement.
  The EditMode suite now contains 54 tests.
- An opt-in PlayMode acceptance suite and reproducible local Supabase scaffold covering Auth,
  PostgREST, Realtime, Storage, and Edge Functions without adding runtime dependencies.

## [0.2.0-beta.4] - 2026-08-14

### Added

- Realtime channels now expose Supabase `system` notices through
  `SystemMessageReceived`, including informational Postgres Changes notices that do not close
  the channel.
- A dependency-free loopback contract fixture now verifies Auth, Database, Storage, and Edge
  Functions request/response boundaries as part of the normal EditMode suite.
- EditMode coverage for channel error recovery, rate-limit cooldowns, expired-token refresh,
  non-retryable failures, pending acknowledgements, and duplicate rejoin prevention. The suite
  now contains 49 tests.

### Fixed

- Unexpected channel errors now rejoin only the affected channel with bounded backoff instead
  of waiting for a socket-wide reconnect.
- Expired Realtime JWTs are refreshed before rejoining, and rate-limited channels wait at
  least 10 seconds before retrying.
- Stale `phx_close` events can no longer create a second subscription after a channel has
  already recovered.
- Pending Realtime acknowledgements now fail immediately when the socket closes or the client
  is disposed, and cancellation no longer leaves an acknowledgement entry behind until its
  timeout expires.

## [0.2.0-beta.3] - 2026-08-12

### Changed

- GitHub-hosted checks are now explicitly license-free and no longer publish successful jobs
  that only skipped Unity because a license secret was unavailable.
- Releases now require a local Unity verification record covering EditMode tests, clean
  `.unitypackage` import, WebGL compilation, and the exact hashes of both published archives.
- Release archives now normalize text line endings so Windows and Linux builds produce the
  same package bytes.
- Corrected the setup, PKCE persistence, WebGL CORS, security-policy, and platform-report
  documentation without changing the package API.

## [0.2.0-beta.2] - 2026-08-09

### Added

- GitHub releases now include a Unity Package Manager tarball and a `.unitypackage` archive.
- Release archives are built and checked automatically from version tags.

### Changed

- Installation and project documentation have been consolidated around the supported package
  formats, runtime requirements and security boundary.

## [0.2.0-beta.1] - 2026-08-04

See [the migration guide](Documentation~/migration-0.2.md) for the two breaking changes.

### Fixed

- OAuth sign-in now survives the operating system terminating a backgrounded app during the
  browser hop. PKCE verifiers are stored durably and independently of `PersistSession`,
  expire after 10 minutes, and are removed once exchanged. Previously the verifier was lost
  on a cold start and the failure message wrongly told the user their callback had reached a
  different device.
- `RefreshSessionAsync` no longer returns `null` when the HTTP transport completes
  synchronously. `ClearRefreshTaskWhenComplete` cleared the shared field from inside the
  caller's reentrant lock, so the caller received `null` and threw. Only reachable through a
  custom `IHttpTransport`; the default transport was unaffected.

### Changed

- **Breaking:** `FileSessionStore` is now `UnencryptedFileSessionStore`. Behavior is
  unchanged; the name now states what it does.
- **Breaking:** `AuthClient`'s constructor takes a PKCE store. Pass `null` for the previous
  behavior. `SupabaseClient` handles this for you.
- The settings inspector now names where a persisted token is written and who can read it,
  per platform, instead of describing it as "an ordinary file".

### Added

- `SupabaseClientOptions.PkceStore` for controlling where OAuth verifiers are stored.
- First test coverage for `AuthClient`: 41 EditMode tests, up from 23, covering password
  auth, OTP, anonymous sign-in, refresh single-flighting, session restore, identity linking,
  MFA, disposal, and the full PKCE lifecycle including cold start and expiry.
- [Session storage documentation](Documentation~/session-storage.md), including per-platform
  exposure and a skeleton for a Keychain or Keystore-backed `ISessionStore`.
- `SUPPORT.md`, `CONTRIBUTING.md`, and issue forms, including a platform report form.

### Security

- Documentation no longer implies that session persistence is backed by Keychain or Keystore.
  It states plainly that the refresh token is written as plain text, and who can read it on
  each platform.
- Added an optional licensed Unity EditMode matrix for `main` and same-repo pull requests. It
  only ran when a `UNITY_LICENSE` secret was available.
- CI fails when `X-Client-Info` drifts from the version in `package.json`.

### Known limitations

- The platform support matrix now records how each target was verified rather than only
  whether it is expected to work. Editor, WebGL, Windows, macOS, Android, and iOS are
  maintainer-verified. Linux is untested. Consoles remain unsupported.
- `SupabaseClient` cannot be used from Editor scripts outside Play mode; the transport drives
  requests through coroutines. No shipped code path is affected. Planned for 0.3.0.
- Blocking on these tasks from the main thread (`.Result`, `.GetAwaiter().GetResult()`)
  deadlocks, because the transport needs the main thread to make progress. Use `await` or the
  coroutine bridge.

## [0.1.0-alpha.1] - 2026-07-22

- Added Unity-first Auth, PostgREST Database, Realtime, Storage and Edge Functions clients.
- Added WebGL localStorage and browser WebSocket bridges.
- Added native `ClientWebSocket` transport, persisted sessions, automatic refresh and reconnect.
- Added Editor configuration validator and OpenAPI model generator.
- Added English and Turkish documentation and a Quickstart sample.
- Kept new publishable API keys out of the JWT Authorization header while retaining legacy anon JWT support.
- Rejected elevated credentials in client configuration, request headers, persisted sessions and build assets.
- Made session persistence opt-in and documented secure native storage requirements.
- Hardened Realtime token changes, reconnects, heartbeat acknowledgements, filtered bindings and Presence merging.
- Sanitized WebGL Auth callback URLs after processing, blocked credentialed redirects, and hardened model generation output boundaries.
- Expanded the Unity 2021-compatible EditMode suite to 23 tests.
