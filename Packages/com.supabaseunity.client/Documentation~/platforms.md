# Platform notes

## WebGL

- HTTP uses `UnityWebRequest`; the Supabase project must allow the deployed origin through CORS.
- Realtime uses the browser `WebSocket` API through `SupabaseUnity.jslib`.
- When persistence is enabled, sessions use browser `localStorage`. Persistence is off by default.
- PKCE verifiers always use `localStorage`, independently of the persistence setting. See [session storage](session-storage.md).
- Auth callback query/fragment credentials are removed from the visible URL after processing.
- Browser security rules control popups, redirects, cookies and mixed content. Serve production builds over HTTPS/WSS.
- Threads are not required by the SDK.

## Desktop and mobile

- Realtime uses `System.Net.WebSockets.ClientWebSocket`.
- Persistence is off by default. If enabled without a custom store, `UnencryptedFileSessionStore` writes the refresh token as plain text under `Application.persistentDataPath/Supabase`. That directory is app-private on Android and iOS, but readable by any process running as the same user on Windows, macOS, and Linux. See [session storage](session-storage.md) for the exposure table and an `ISessionStore` skeleton.
- OAuth opens the system browser through `Application.OpenURL`; configure `Application.deepLinkActivated` support in the player and add that callback URI to Supabase Auth.
- The OS may terminate a backgrounded app during that browser hop. PKCE verifiers are therefore stored durably regardless of the persistence setting, so the deep link can still complete sign-in after a cold start.

## Unity versions

The runtime assembly avoids editor-only APIs and targets the Unity 2021.3 API surface. The CI workflow is configured to compile/test the oldest supported LTS, a current intermediate LTS and Unity 6. Hardware validation remains separate from compilation. The package does not support consoles until their networking and secure-storage constraints are validated on hardware.
