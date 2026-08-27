# API guide

Network methods below return `Task<SupabaseResult>` or `Task<SupabaseResult<T>>` and accept an
optional `CancellationToken` as the final argument. Local helpers, such as `GetPublicUrl`, return
their value directly.

## Client

```csharp
var client = new SupabaseClient(settings);
var ready = await client.InitializeAsync();
```

`InitializeAsync` restores Auth state and, when enabled in settings, opens Realtime. Dispose the client when its owning game subsystem is destroyed.

## Auth

```csharp
await client.Auth.SignUpWithPasswordAsync(email, password, new AuthSignUpOptions
{
    Data = new JObject { ["display_name"] = name },
    EmailRedirectTo = "mygame://auth"
});

await client.Auth.SignInWithPasswordAsync(email, password);
await client.Auth.SignInWithOtpAsync(email);
await client.Auth.VerifyOtpAsync(code, AuthOtpType.Email, email);
await client.Auth.SignOutAsync(AuthSignOutScope.Local);
```

`Global` and `Local` sign-out clear the current local session. `Others` revokes every other
session for the user while preserving the current session and does not emit `SignedOut`.

Concurrent `RefreshSessionAsync` calls share one token request. Canceling one caller stops that
caller's wait without canceling the refresh for other callers; disposing the client cancels it.

When session-changing Auth calls overlap, the operation started most recently owns the local
session. An older response that arrives later returns `auth_operation_superseded` instead of
replacing or clearing newer state. `StateChanged` notifications follow the same accepted order;
superseded operations do not emit an event for a newer session.

After an Auth operation adopts a new local session state, its durable session-store commit is the
operation's commit point and is allowed to finish even if that caller cancels. Cancellation before
the session is adopted still cancels the request normally.

Session-store reads, writes, and removals are serialized. A delayed startup read is ignored if
the local Auth session changes before that read finishes, so restarting the game cannot restore
state that already lost an in-memory ordering decision.

Profile and identity responses are applied only while the same user is active and no newer user
operation has started. Otherwise, the older call returns `auth_operation_superseded`. A token
refresh for the same user does not supersede the response.

Auth failures use `SupabaseError.Code` for both server codes and stable client codes such as
`not_authenticated`, `auth_operation_superseded`, `auth_session_missing`,
`auth_response_invalid`, `auth_session_restore_failed`, `auth_callback_provider_missing`, and
`pkce_verifier_missing`. OAuth callback errors retain the provider's standard OAuth error code.

OAuth uses PKCE and returns the authorization URI whether or not `OpenBrowser` is enabled:

```csharp
var oauth = await client.Auth.SignInWithOAuthAsync("discord", new AuthOAuthOptions
{
    RedirectTo = "mygame://auth",
    Scopes = "identify email",
    OpenBrowser = true
});
```

Other methods cover anonymous sign-in, ID-token sign-in, SSO, password recovery, user updates, session refresh/restore, link/unlink/list identities, TOTP/phone MFA enrollment, challenge, verification, unenrollment and assurance-level inspection. `CurrentSession`, `CurrentUser` and `StateChanged` provide local state.

## Database

`client.From<T>()` reads `[SupabaseTable]`; an explicit table name overrides it. `[SupabaseColumn]` maps model members.

Available filters include `Eq`, `Neq`, `Gt`, `Gte`, `Lt`, `Lte`, `Like`, `ILike`, `Is`, `In`, `Contains`, `ContainedBy`, `Overlaps`, `TextSearch`, `Not`, `Match`, and raw PostgREST `Or` expressions. Modifiers include `Select`, `Order`, `Limit`, `Offset`, `Range`, and `Count`.

```csharp
var page = await client.From<Profile>()
    .Select("id,username,team:team_id(name)")
    .Eq("active", true)
    .Or("score.gte.100,role.eq.moderator")
    .Range(0, 49)
    .Count(PostgrestCount.Exact)
    .GetAsync();

var one = await client.From<Profile>().Eq("id", id).SingleAsync();
var optional = await client.From<Profile>().Eq("handle", handle).MaybeSingleAsync();
var csv = await client.From<Profile>().Select("id,username").GetCsvAsync();
```

Writes return rows by default. Use `PostgrestWriteOptions` for minimal responses, counts, conflict columns, duplicate behavior and missing-column behavior.

```csharp
await client.From<Profile>().InsertAsync(profile);
await client.From<Profile>().UpsertAsync(profile, new PostgrestWriteOptions { OnConflict = "id" });
await client.From<Profile>().Eq("id", id).UpdateAsync(new { username = newName });
await client.From<Profile>().Eq("id", id).DeleteAsync();
await client.Database.RpcAsync<Leaderboard>("leaderboard", new { season_id = seasonId });
```

## Realtime

Configure Postgres bindings before subscribing. Broadcast handlers may be added at any time.

```csharp
var room = client.Realtime.Channel("match:42", new RealtimeChannelConfig
{
    BroadcastAcknowledge = true,
    BroadcastSelf = false,
    PresenceKey = playerId
});

room.OnBroadcast("move", payload => ApplyMove(payload));
room.OnPostgresChanges(new RealtimePostgresChangeFilter
{
    Event = RealtimePostgresEvent.Update,
    Schema = "public",
    Table = "matches",
    Filter = "id=eq.42"
}, change => RefreshMatch(change.New<Match>()));

room.PresenceSynchronized += state => RefreshPlayers(state);
room.SystemMessageReceived += message =>
{
    if (message.IsError)
        Debug.LogWarning($"Realtime {message.Extension}: {message.Message}");
};
await room.SubscribeAsync();
await room.TrackAsync(new { player_id = playerId, online_at = DateTimeOffset.UtcNow });
await room.SendBroadcastAsync("move", new { x = 4, y = 8 });
```

The client sends heartbeats, reconnects the socket with backoff, and rejoins subscribed
channels. A channel-level `phx_error` or unexpected `phx_close` rejoins only that channel.
Expired JWT notices refresh the Auth session before rejoining, while rate-limit notices apply
a cooldown of at least 10 seconds.

`SystemMessageReceived` exposes every server `system` notice. A failed `postgres_changes`
extension is a degraded-service notice and does not close the channel. Unknown configuration
or payload errors remain closed after the notice instead of retrying forever; correct the
reported problem and call `SubscribeAsync` again.

## Storage

```csharp
var avatars = client.Storage.From("avatars");
var upload = await avatars.UploadAsync($"{userId}/avatar.png", pngBytes,
    new StorageUploadOptions
    {
        ContentType = "image/png",
        CacheControl = "3600",
        Upsert = true,
        Progress = new Progress<float>(value => SetProgress(value))
    });

var bytes = await avatars.DownloadAsync(path);
var files = await avatars.ListAsync(userId, new StorageListOptions { Limit = 100 });
var signed = await avatars.CreateSignedUrlAsync(path, 60);
var publicUri = avatars.GetPublicUrl(path, new StorageTransformOptions { Width = 256, Height = 256 });
```

`StorageClient` manages buckets. `StorageBucketClient` also supports info, update, move, cross-bucket copy, remove, bulk signed URLs, signed-upload URLs and image render transforms.

## Functions

```csharp
var response = await client.Functions.InvokeAsync<RewardResponse>("claim-reward",
    new FunctionInvokeOptions
    {
        Body = new { reward_id = rewardId },
        Headers = { ["x-client-build"] = Application.version },
        Timeout = TimeSpan.FromSeconds(15)
    });
```

Use the non-generic overload to receive raw bytes, response headers and status. `RawBody` supports binary payloads.

## Coroutines

```csharp
StartCoroutine(client.From<Profile>().GetAsync().AsCoroutine(
    result => HandleProfiles(result),
    exception => Debug.LogException(exception)));
```

For IL2CPP builds, model members used only through reflection must be preserved. The Editor generator adds `[UnityEngine.Scripting.Preserve]` automatically; add it manually to handwritten models when using aggressive managed stripping.

## API stability

`0.2.0-rc.1` freezes the public C# surface for `0.2.0`. Later release candidates and the final
`0.2.0` release may add members, but they will not remove, rename, or change existing public
signatures. A compatibility test records the approved types, members, enum values, optional
defaults, generic constraints, and declared interfaces.

The project is still pre-1.0. A future `0.x` minor release may contain documented breaking
changes, while patch releases remain backward compatible.
