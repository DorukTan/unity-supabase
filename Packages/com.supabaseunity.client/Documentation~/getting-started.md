# Getting started

## 1. Configure the project

Expose only the schemas/tables the game needs through the Data API. Enable Row Level Security and write policies for `anon` and `authenticated` before shipping a player build. Supabase's [Data API security guide](https://supabase.com/docs/guides/api/securing-your-api) explains grants and Row Level Security.

In Unity, open **Tools > Supabase > Setup**. Create or select a `SupabaseSettings` asset and enter:

- Project URL, for example `https://example.supabase.co`
- Publishable key (`sb_publishable_...`) or a legacy `anon` JWT
- Default PostgREST schema (normally `public`)

Copy the URL and key from the Supabase Dashboard **Connect** dialog, then click **Test Project
Connection**. This checks the client configuration and network path without sending an elevated
key. It does not test a table or its policies.

Call `InitializeAsync` once near game startup. When persistence is explicitly enabled, this restores an existing session and refreshes it when needed. It can also optionally connect Realtime.

## 2. Run the Quickstart

UPM users can import **Quickstart** from the package's **Samples** tab in Package Manager. Run its
`setup.sql` in the Supabase SQL Editor, attach `SupabaseQuickstart` to an empty GameObject, assign
the settings asset, and press Play. The sample queries `public.scores` and subscribes to its
Postgres Changes.

The `.unitypackage` distribution includes the same files at
`Assets/SupabaseUnity/Samples/Quickstart`; no separate sample import is required.

The sample policy is deliberately readable by `anon` and `authenticated` clients. Replace it with
user-specific policies before adapting the table for real player data.

## 3. Handle results

Every network operation returns `SupabaseResult` or `SupabaseResult<T>`. Transport, HTTP, protocol and serialization failures are values, so routine server errors do not need exception handling. Cancellation still raises `OperationCanceledException`.

```csharp
var result = await client.Database.RpcAsync<PlayerStats>("player_stats", new { player_id = id });
if (!result.IsSuccess)
{
    Debug.LogError($"{result.Error.Code}: {result.Error.Message}");
    return;
}
```

`result.Metadata.StatusCode`, `Headers`, and `Count` expose response metadata. Call `GetValueOrThrow()` only when exception-based flow is preferred.

## 4. Database

Queries follow PostgREST syntax and can select embedded relationships:

```csharp
var result = await client.From<Match>("matches")
    .Select("id,created_at,players!inner(username)")
    .Eq("season_id", seasonId)
    .Gte("score", 100)
    .Order("created_at", ascending: false)
    .Range(0, 49)
    .Count(PostgrestCount.Exact)
    .GetAsync();
```

Write filters are applied before `UpdateAsync` and `DeleteAsync`. Always filter mutations unless changing every row is intentional.

## 5. Auth and OAuth

Password, OTP, OAuth with PKCE, SSO, anonymous sign-in, recovery, identity linking and MFA are on `client.Auth`. On mobile/desktop, configure a deep-link URL accepted by the Supabase Auth redirect allow-list. WebGL reads and sanitizes the initial browser callback URL. PKCE verifiers always use `localStorage`; sessions use it only when persistence is enabled.

Subscribe to `client.Auth.StateChanged` to update UI. Realtime channel tokens can be updated after an auth transition with `client.Realtime.SetAuthAsync()`.

## 6. Storage and Functions

Use `client.Storage.From("avatars")` for object operations. Upload and download options accept `IProgress<float>`. Download also accepts a chunk callback for streaming consumption.

Edge Functions always receive the configured key in `apikey`. `Authorization` is added only when an authenticated user JWT exists; a new `sb_publishable_...` key is never sent as a bearer token. Public functions must use the appropriate `verify_jwt` setting and implement their intended authorization policy. See Supabase's [authorization header guide](https://supabase.com/docs/guides/functions/auth-headers).

If something fails, use the [troubleshooting guide](troubleshooting.md) before changing keys or
loosening a policy.
