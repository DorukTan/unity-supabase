# Supabase for Unity

An independent Supabase client for Unity 2021.3 and newer. The package provides Auth,
Database, Realtime, Storage and Edge Functions APIs for WebGL, desktop and mobile projects.

> The package is in beta. Review the [changelog](CHANGELOG.md) before upgrading.

## Installation

In Unity, open **Window > Package Manager**, select **Add package from git URL**, and enter:

```text
https://github.com/DorukTan/unity-supabase.git?path=/Packages/com.supabaseunity.client#v0.2.0-beta.3
```

Release tarballs and `.unitypackage` files are available on the
[GitHub Releases page](https://github.com/DorukTan/unity-supabase/releases). When importing the
`.unitypackage`, install `com.unity.nuget.newtonsoft-json` 3.2.1 or newer first. Do not install
the `.unitypackage` alongside the UPM distribution.

## Configuration

Create a settings asset with **Assets > Create > Supabase > Settings**. Set the project URL,
publishable key and Data API schema, then assign the asset to the component that initializes
the client. Legacy `anon` JWT keys are also supported.

Never put an `sb_secret_...` key, `service_role` key, database password or management token in
a Unity project. Player builds are public clients and must be protected with Row Level
Security, Storage policies and server-side authorization.

## Example

```csharp
using System;
using Supabase.Unity;

[Serializable, SupabaseTable("cities")]
public sealed class City
{
    [SupabaseColumn("id")]
    public long Id { get; set; }

    [SupabaseColumn("name")]
    public string Name { get; set; }
}

var client = new SupabaseClient(settings);
var initialized = await client.InitializeAsync();

if (!initialized.IsSuccess)
{
    UnityEngine.Debug.LogError(initialized.Error);
    return;
}

var cities = await client.From<City>()
    .Select("id,name")
    .Order("name")
    .Limit(50)
    .GetAsync();

if (!cities.IsSuccess)
    UnityEngine.Debug.LogError(cities.Error);
```

Network operations return `SupabaseResult` or `SupabaseResult<T>`. Check `IsSuccess` before
reading `Data` or `Error`. Cancellation throws `OperationCanceledException`.

Do not block network tasks with `.Result` or `.GetAwaiter().GetResult()` on Unity's main
thread. Use `await` or the provided coroutine bridge.

## Services

- **Auth:** password, OTP, anonymous sign-in, OAuth/PKCE, SSO, identity linking, recovery,
  MFA, refresh and optional session persistence.
- **Database:** typed PostgREST select, filter, ordering, pagination, CRUD and RPC operations.
- **Realtime:** Postgres Changes, Broadcast and Presence over Phoenix channels, including
  heartbeat, reconnect and channel rejoin.
- **Storage:** bucket management, upload, download, listing, move, copy, removal, image
  transforms and signed URLs.
- **Functions:** typed and raw Edge Function requests with custom methods, bodies, headers
  and timeouts.

## Documentation

- [Getting started](Documentation~/getting-started.md)
- [API guide](Documentation~/api.md)
- [Platform notes](Documentation~/platforms.md)
- [Session storage](Documentation~/session-storage.md)
- [Security model](Documentation~/security.md)
- [Migration guide](Documentation~/migration-0.2.md)
- [Turkish quick start](README.tr.md)
- [Changelog](CHANGELOG.md)

The main repository contains the complete
[support and contribution policies](https://github.com/DorukTan/unity-supabase).

MIT licensed. This project is not affiliated with or endorsed by Supabase, Inc. or Unity
Technologies.
