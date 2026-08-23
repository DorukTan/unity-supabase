# Platform notes

## WebGL

- HTTP uses `UnityWebRequest`. Browser CORS rules apply; Edge Functions called from WebGL must return the appropriate CORS and preflight headers for the deployed origin.
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

The runtime assembly avoids editor-only APIs and targets the Unity 2021.3 API surface. Unity compilation and tests run locally on Hub-activated editors; GitHub-hosted checks are intentionally license-free. New tagged releases record the exact editor version and test count used by the local release gate. That gate imports the generated `.unitypackage` into a clean project, builds WebGL, Windows, and Android smoke players, and generates an iOS Xcode project. The iOS result proves Unity compilation and export only; it does not replace Xcode compilation, signing, or a hardware run. Compatibility checks on Unity 2021.3 and 2022.3 are maintainer-run before stable compatibility claims, while the project version is the release gate used for every tag. Hardware validation remains separate from compilation. The package does not support consoles until their networking and secure-storage constraints are validated on hardware.
