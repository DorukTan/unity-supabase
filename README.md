# Supabase for Unity

[![Release](https://img.shields.io/github/v/release/DorukTan/unity-supabase?include_prereleases)](https://github.com/DorukTan/unity-supabase/releases)
[![OpenUPM](https://img.shields.io/npm/v/com.supabaseunity.client?label=openupm&registry_uri=https://package.openupm.com)](https://openupm.com/packages/com.supabaseunity.client/)
[![Package integrity](https://github.com/DorukTan/unity-supabase/actions/workflows/package-checks.yml/badge.svg)](https://github.com/DorukTan/unity-supabase/actions/workflows/package-checks.yml)
[![Unity](https://img.shields.io/badge/Unity-2021.3%20to%206-222222?logo=unity)](#requirements)
[![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE.md)

An independent Supabase client for Unity. It provides Auth, Database, Realtime, Storage and
Edge Functions APIs for WebGL, desktop and mobile projects.

> This package is in beta. The public API may change before version 1.0. Breaking changes are
> documented in the [changelog](Packages/com.supabaseunity.client/CHANGELOG.md).

This project is not affiliated with or endorsed by Supabase, Inc. or Unity Technologies.

## Features

| Module | Support |
| --- | --- |
| Auth | Password, OTP, anonymous sign-in, OAuth/PKCE, SSO, MFA, recovery and sessions |
| Database | Typed PostgREST queries, filters, relationships, pagination, CRUD, counts, CSV and RPC |
| Realtime | Postgres Changes, Broadcast, Presence, reconnects and channel rejoin |
| Storage | Upload, download, progress, listing, move, copy, transforms and signed URLs |
| Functions | Typed and raw Edge Function requests with custom methods, headers and timeouts |
| Editor tools | Settings validation and OpenAPI model generation |

The runtime uses `UnityWebRequest` for HTTP, a browser bridge for WebGL WebSockets and
`ClientWebSocket` on supported native targets. It does not depend on the official Supabase
.NET client.

## Requirements

- Unity 2021.3 or newer.
- A Supabase project.
- `com.unity.nuget.newtonsoft-json` 3.2.1 or newer. Unity Package Manager resolves this
  automatically for Git, OpenUPM and tarball installations.

## Installation

### Git URL

In Unity, open **Window > Package Manager**, select **Add package from git URL**, and enter:

```text
https://github.com/DorukTan/unity-supabase.git?path=/Packages/com.supabaseunity.client#v0.2.0-beta.2
```

### OpenUPM

```bash
openupm add com.supabaseunity.client
```

### Release archives

Each [GitHub release](https://github.com/DorukTan/unity-supabase/releases) includes two
installable archives:

- `com.supabaseunity.client-<version>.tgz` for **Package Manager > Add package from tarball**.
- `com.supabaseunity.client-<version>.unitypackage` for **Assets > Import Package > Custom
  Package**. Install `com.unity.nuget.newtonsoft-json` first when using this option.

The `.unitypackage` installs to `Assets/SupabaseUnity`. Do not install it alongside the UPM
package; both distributions contain the same assemblies.

## Quick start

Create a settings asset with **Assets > Create > Supabase > Settings** and assign the project
URL and publishable key. Then initialize one client and access the required service:

```csharp
using System;
using Supabase.Unity;
using UnityEngine;

public sealed class Leaderboard : MonoBehaviour
{
    [SerializeField] private SupabaseSettings settings;
    private SupabaseClient client;

    private async void Start()
    {
        client = new SupabaseClient(settings);

        var initialized = await client.InitializeAsync();
        if (!initialized.IsSuccess)
        {
            Debug.LogError(initialized.Error);
            return;
        }

        var scores = await client.From<ScoreRow>()
            .Select("id,player_name,score")
            .Order("score", ascending: false)
            .Limit(10)
            .GetAsync();

        if (scores.IsSuccess)
            Debug.Log($"Loaded {scores.Data.Count} scores.");
        else
            Debug.LogError(scores.Error);
    }

    private void OnDestroy()
    {
        client?.Dispose();
    }
}

[Serializable, SupabaseTable("scores")]
public sealed class ScoreRow
{
    [SupabaseColumn("id")]
    public long Id { get; set; }

    [SupabaseColumn("player_name")]
    public string PlayerName { get; set; }

    [SupabaseColumn("score")]
    public int Score { get; set; }
}
```

Network calls return `SupabaseResult` or `SupabaseResult<T>`. Check `IsSuccess` before reading
`Data` or `Error`. Cancellation is reported with `OperationCanceledException`.

Do not block these tasks with `.Result` or `.GetAwaiter().GetResult()` on Unity's main thread.
The transport requires the main thread to continue running.

The Quickstart sample is available from the package's **Samples** tab in Package Manager.

## Security

A Unity player is a public client. Never include an `sb_secret_...` key, `service_role` key,
database password or management token in a project. Use database grants, Row Level Security,
Storage policies and Edge Function authorization to control access.

The package rejects elevated keys in configuration, headers, persisted sessions and player
builds. It also requires HTTPS/WSS outside local development and removes credentials from SDK
error messages. These checks do not replace server-side authorization.

Session persistence is disabled by default. Review the
[session storage guide](Packages/com.supabaseunity.client/Documentation~/session-storage.md)
before enabling it.

Report vulnerabilities through
[GitHub private vulnerability reporting](https://github.com/DorukTan/unity-supabase/security/advisories/new).
Do not include live credentials in a report.

## Platform support

| Target | Status |
| --- | --- |
| Unity 2021.3 LTS, 2022.3 LTS and Unity 6 Editor | Supported |
| WebGL | Supported |
| Windows, macOS, Android and iOS players | Supported |
| Linux player | Compiles; runtime verification is pending |
| Consoles | Unsupported |

Platform details and limitations are maintained in the
[platform guide](Packages/com.supabaseunity.client/Documentation~/platforms.md).

## Documentation

- [Getting started](Packages/com.supabaseunity.client/Documentation~/getting-started.md)
- [API guide](Packages/com.supabaseunity.client/Documentation~/api.md)
- [Platform notes](Packages/com.supabaseunity.client/Documentation~/platforms.md)
- [Session storage](Packages/com.supabaseunity.client/Documentation~/session-storage.md)
- [Security model](Packages/com.supabaseunity.client/Documentation~/security.md)
- [Migration guide](Packages/com.supabaseunity.client/Documentation~/migration-0.2.md)
- [Turkish quick start](Packages/com.supabaseunity.client/README.tr.md)
- [Changelog](Packages/com.supabaseunity.client/CHANGELOG.md)

## Contributing and support

See [CONTRIBUTING.md](CONTRIBUTING.md) before opening a pull request. Use GitHub issues for
bug reports, support requests and platform reports. The support scope is documented in
[SUPPORT.md](SUPPORT.md).

## License and trademarks

The project is licensed under the [MIT License](LICENSE.md).

Supabase and the Supabase logo are trademarks of Supabase, Inc. Unity and the Unity logo are
trademarks or registered trademarks of Unity Technologies. Their names identify compatibility
only and do not imply sponsorship or endorsement.
