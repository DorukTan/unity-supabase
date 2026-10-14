# Supabase for Unity

An independent Supabase client for Unity 2021.3 and newer. The package provides Auth,
Database, Realtime, Storage and Edge Functions APIs for WebGL, desktop and mobile projects.

> `0.3.0` adds [Editor scripting](Documentation~/editor-scripting.md) while preserving the
> `0.2.0` runtime API. Review the [changelog](CHANGELOG.md) before upgrading.

## Installation

In Unity 2021.3 or 2022.3, open **Window > Package Manager**. In Unity 6, open
**Window > Package Management > Package Manager**. Select **Add package from git URL**, then enter:

```text
https://github.com/DorukTan/unity-supabase.git?path=/Packages/com.supabaseunity.client#v0.3.0
```

Release tarballs and `.unitypackage` files are available on the
[GitHub Releases page](https://github.com/DorukTan/unity-supabase/releases). When importing the
`.unitypackage`, install `com.unity.nuget.newtonsoft-json` 3.2.1 or newer first. Do not install
the `.unitypackage` alongside the UPM distribution.

## Configuration

Open **Tools > Supabase > Setup**. Create or select a settings asset, set the project URL,
publishable key and Data API schema, then run **Test Project Connection**. Assign the asset to the
component that initializes the client. Legacy `anon` JWT keys are also supported.

UPM users can import **Quickstart** from the package's **Samples** tab. It contains repeatable SQL,
a Database query, a Realtime subscription, and the steps needed to run both.
The `.unitypackage` distribution includes the same sample at
`Assets/SupabaseUnity/Samples/Quickstart`.

Never put an `sb_secret_...` key, `service_role` key, database password or management token in
a Unity project. Player builds are public clients and must be protected with Row Level
Security, Storage policies and server-side authorization.

## Example

Save this component as `Leaderboard.cs`, attach it to a GameObject, and assign settings.
Create its `scores` table with the [Quickstart SQL](Samples~/Quickstart/setup.sql).

```csharp
using System;
using System.Threading;
using Supabase.Unity;
using UnityEngine;

public sealed class Leaderboard : MonoBehaviour
{
    [SerializeField] private SupabaseSettings settings;
    private SupabaseClient client;
    private CancellationTokenSource lifetime;

    private async void Start()
    {
        if (settings == null)
        {
            Debug.LogError("Assign a SupabaseSettings asset.", this);
            return;
        }

        lifetime = new CancellationTokenSource();
        var token = lifetime.Token;
        try
        {
            client = new SupabaseClient(settings);
            var initialized = await client.InitializeAsync(token);
            token.ThrowIfCancellationRequested();
            if (!initialized.IsSuccess)
            {
                Debug.LogError(initialized.Error, this);
                return;
            }

            var scores = await client.From<ScoreRow>()
                .Select("id,player_name,score")
                .Order("score", ascending: false)
                .Limit(10)
                .GetAsync(token);
            token.ThrowIfCancellationRequested();
            if (scores.IsSuccess)
                Debug.Log("Loaded " + scores.Data.Count + " scores.", this);
            else
                Debug.LogError(scores.Error, this);
        }
        catch (OperationCanceledException)
        {
            // The component's lifetime ended.
        }
        catch (SupabaseConfigurationException exception)
        {
            Debug.LogError(exception.Message, this);
        }
    }

    private void OnDestroy()
    {
        if (lifetime != null) lifetime.Cancel();
        if (client != null) client.Dispose();
        if (lifetime != null) lifetime.Dispose();
    }
}

[Serializable, SupabaseTable("scores")]
public sealed class ScoreRow
{
    [SupabaseColumn("id")] public long Id { get; set; }
    [SupabaseColumn("player_name")] public string PlayerName { get; set; }
    [SupabaseColumn("score")] public int Score { get; set; }
}
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
- [Quickstart sample](Samples~/Quickstart/README.md)
- [Troubleshooting](Documentation~/troubleshooting.md)
- [API guide](Documentation~/api.md)
- [Editor scripting](Documentation~/editor-scripting.md)
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
