# Migrating to 0.2.0-beta.1

Two breaking changes, both narrow. If you never named these types directly, and most
projects do not, you need no code changes at all.

## `FileSessionStore` is now `UnencryptedFileSessionStore`

Only affects you if you constructed it explicitly:

```csharp
// Before
options.SessionStore = new FileSessionStore();

// After
options.SessionStore = new UnencryptedFileSessionStore();
```

Behavior is identical. The rename exists because the old name said nothing about what the
type does: it writes your refresh token to disk as plain text. On Android and iOS that
location is app-private; on Windows, macOS, and Linux it is readable by any process running
as the same user. You should not be able to select that without noticing.

Nothing changes if you set `PersistSession = true` and let the package choose a store, other
than the class name appearing in stack traces.

See [session storage](session-storage.md) for the exposure table and a skeleton for writing
a Keychain or Keystore-backed store.

## `AuthClient`'s constructor takes a PKCE store

`AuthClient` is constructed by `SupabaseClient`. This only affects you if you were
constructing it yourself, which is not a supported pattern:

```csharp
// Before
new AuthClient(options, endpoint, transport, sessionStore, callbackProvider);

// After
new AuthClient(options, endpoint, transport, sessionStore, pkceStore, callbackProvider);
```

Pass `null` for `pkceStore` to fall back to the session store, which is the old behavior.

## Behavior change worth knowing about, even though nothing breaks

**OAuth sign-in now survives the app being killed.**

PKCE verifiers previously lived in the session store, which is in-memory unless
`PersistSession` is set. `Application.OpenURL` backgrounds your game, and Android and iOS
routinely terminate backgrounded apps. When the player returned via deep link, the verifier
was gone and they were told:

> The PKCE verifier is missing. The callback must be handled on the same device that started OAuth.

They were on the same device. Nothing they did was wrong.

Verifiers now go to durable platform storage regardless of `PersistSession`, expire after 10
minutes, and are deleted once exchanged. You can override the location with
`SupabaseClientOptions.PkceStore`.

This is a security-relevant change in that it writes something to disk that previously was
not written. A verifier is single-use and worthless after exchange, so it does not carry the
risk that persisting a refresh token does, but you should know it happens.

**`RefreshSessionAsync` no longer returns null.**

If you supplied your own `IHttpTransport` that completes synchronously, such as a cache, an
offline stub, or a fake in your own tests, `RefreshSessionAsync()` could return `null` and
throw `NullReferenceException` at the call site. It now always returns the task it started.
Projects using the default transport were never affected.
