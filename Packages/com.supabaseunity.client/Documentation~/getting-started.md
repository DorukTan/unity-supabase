# Getting started

## 1. Configure Supabase

Expose only the schemas/tables the game needs through the Data API. Enable Row Level Security and write policies for `anon` and `authenticated` before shipping a player build. The official [C# Data API setup](https://supabase.com/docs/reference/csharp/installing) explains the required grants.

Create a `SupabaseSettings` asset and enter:

- Project URL, for example `https://example.supabase.co`
- Publishable key (`sb_publishable_...`) or a legacy `anon` JWT
- Default PostgREST schema (normally `public`)

Call `InitializeAsync` once near game startup. When persistence is explicitly enabled, this restores an existing session and refreshes it when needed. It can also optionally connect Realtime.

## 2. Handle results

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

## 3. Database

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

## 4. Auth and OAuth

Password, OTP, OAuth with PKCE, SSO, anonymous sign-in, recovery, identity linking and MFA are on `client.Auth`. On mobile/desktop, configure a deep-link URL accepted by the Supabase Auth redirect allow-list. WebGL reads and sanitizes the initial browser callback URL. When persistence is enabled, WebGL stores the PKCE verifier and session in `localStorage`.

Subscribe to `client.Auth.StateChanged` to update UI. Realtime channel tokens can be updated after an auth transition with `client.Realtime.SetAuthAsync()`.

## 5. Storage and Functions

Use `client.Storage.From("avatars")` for object operations. Upload and download options accept `IProgress<float>`. Download also accepts a chunk callback for streaming consumption.

Edge Functions always receive the configured key in `apikey`. `Authorization` is added only when an authenticated user JWT exists; a new `sb_publishable_...` key is never sent as a bearer token. Public functions must use the appropriate `verify_jwt` setting and implement their intended authorization policy. See Supabase's [authorization header guide](https://supabase.com/docs/guides/functions/auth-headers).
