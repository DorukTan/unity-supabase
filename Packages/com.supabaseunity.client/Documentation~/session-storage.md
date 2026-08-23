# Session storage

A Supabase session contains an access token (short-lived) and a refresh token (long-lived).
The refresh token is the sensitive one: anyone holding it can mint new access tokens until
it is revoked server-side.

Session persistence is **off by default**. With it off, signing out or closing the game
loses the session and the player signs in again. That is the safest default, and it is the
right one for many games.

## What ships

| Store | Selected when | Where it writes |
| --- | --- | --- |
| `MemorySessionStore` | `PersistSession` is false (default) | Process memory only |
| `UnencryptedFileSessionStore` | `PersistSession` is true, native platforms | `Application.persistentDataPath/Supabase` |
| `WebLocalStorageSessionStore` | `PersistSession` is true, WebGL | Browser `localStorage` |

Assign your own with `SupabaseClientOptions.SessionStore`.

## Exposure by platform

`UnencryptedFileSessionStore` writes plain text. The name is deliberate: you should not be
able to adopt it without noticing what it does.

| Platform | Who can read it |
| --- | --- |
| Android, iOS | The app only. `persistentDataPath` is app-private sandboxed storage. Root or jailbreak defeats this, as does an unrestricted cloud backup (see below). |
| Windows, macOS, Linux | **Any process running as the same user.** Other games, any installed tool, and the player themselves. |
| WebGL | Any script executing on the same origin. An XSS on your page is a token theft. This is what `supabase-js` does too. |

Mobile is roughly what `supabase_flutter` does by default. Desktop is the one that should
give you pause: a refresh token in `AppData/LocalLow` stays valid until revoked.

### Android auto-backup

Unity's generated `AndroidManifest.xml` does not set `android:allowBackup="false"`, so files
under `persistentDataPath`, including a persisted session, may be copied to the player's
Google Drive backup. Set `allowBackup` to `false` in a custom manifest, or exclude the
`Supabase` directory with a backup rules file, if that is not acceptable for your game.

## Writing a platform-secure store

Platform keystore implementations are **intentionally not shipped**. They require JNI and
Objective-C bridges against APIs that change on Google's and Apple's schedules, and getting
them wrong is worse than not having them. Your app is better positioned to own that code,
and `ISessionStore` is the seam for it.

The interface is three methods:

```csharp
using System.Threading;
using System.Threading.Tasks;
using Supabase.Unity;

public sealed class KeystoreSessionStore : ISessionStore
{
    public Task<string> GetAsync(string key, CancellationToken cancellationToken = default(CancellationToken))
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Android: read via EncryptedSharedPreferences or a Keystore-wrapped AES key.
        // iOS:     SecItemCopyMatching against the Keychain.
        // Return null when absent. Do not throw.
        return Task.FromResult<string>(null);
    }

    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default(CancellationToken))
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Overwrite any existing entry for this key.
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default(CancellationToken))
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Must succeed even when nothing is stored. Called on sign-out.
        return Task.CompletedTask;
    }
}
```

Wire it up before constructing the client:

```csharp
var options = settings.ToOptions();
options.PersistSession = true;
options.SessionStore = new KeystoreSessionStore();
var client = new SupabaseClient(options);
```

Contract notes:

- `GetAsync` returns `null` for a missing key. It must not throw.
- `RemoveAsync` on a key that was never written must succeed quietly.
- Keys are opaque strings derived from your project URL. Do not parse them.
- `AuthClient` serializes its `GetAsync`, `SetAsync`, and `RemoveAsync` calls for the session key.
  A startup value is ignored if the local Auth session changes while `GetAsync` is pending.
- After Auth accepts a session change in memory, the corresponding `SetAsync` or `RemoveAsync`
  finishes as a commit step even if the original caller cancels.
- Calls may arrive from a background thread. If your implementation touches Unity APIs,
  marshal to the main thread yourself.

If you write one, a pull request adding it to this page as a community-contributed example
is welcome.

## PKCE verifiers are stored separately

OAuth PKCE verifiers do **not** follow `PersistSession`. They are always written to durable
platform storage through `SupabaseClientOptions.PkceStore`.

This is deliberate. `Application.OpenURL` backgrounds your game, and Android or iOS is free
to terminate it while the player is in the browser. If the verifier lived only in memory,
the deep link would return to a fresh process with nothing to exchange, and sign-in would
fail through no fault of the player.

A verifier is short-lived, single-use, and worthless to an attacker once exchanged, so
persisting it does not carry the risk that persisting a refresh token does. Verifiers expire
after 10 minutes and are deleted on successful exchange.
