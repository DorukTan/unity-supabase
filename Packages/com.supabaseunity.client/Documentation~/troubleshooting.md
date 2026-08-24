# Troubleshooting

Start with the first Supabase error in the Unity Console. `SupabaseError` includes the service,
error code, message, HTTP status when available, and whether the operation may be retried. Do not
post access tokens, refresh tokens, callback URLs, passwords, or raw request headers in an issue.

## The package does not compile after installation

- Do not install the UPM package and `.unitypackage` together. Both contain the same assemblies.
- A `.unitypackage` install requires `com.unity.nuget.newtonsoft-json` 3.2.1 or newer. The UPM
  install brings it in automatically.
- Delete only the duplicate distribution, let Unity finish importing, and check the first compiler
  error rather than the errors that follow it.

## Settings are missing or invalid

Open **Tools > Supabase > Setup**. Create or select a `SupabaseSettings` asset, then copy the
Project URL and `sb_publishable_...` key from the Supabase Dashboard **Connect** dialog. Legacy
`anon` JWTs work, but new projects should use a publishable key.

The setup assistant validates the format locally before enabling its connection test. An
`sb_secret_...` key, `service_role` JWT, database password, or management token must never be in a
Unity project.

## The connection test cannot reach the project

- Confirm the Project URL starts with `https://` and has no path, query string, or fragment.
- Copy the URL and publishable key again instead of editing either value by hand.
- Check whether the Supabase project is paused and whether a proxy or firewall blocks Unity.
- For local Supabase, use its loopback URL and make sure the local stack is running.

The test calls the project's Auth settings endpoint. A successful test proves that the URL, key,
and network path work. It does not prove that a table, RLS policy, Storage policy, or Edge Function
is configured correctly.

## A Database or Storage request returns 401 or 403

The publishable key identifies the client; it does not grant access by itself. Check database
grants, Row Level Security, Storage policies, and whether the current Auth session is the one your
policy expects. Test both the `anon` and `authenticated` roles when your game supports both.

Do not fix a policy problem by putting an elevated key in the player. Move privileged work to a
server or Edge Function and authorize it there.

## A query succeeds but returns no rows

- Confirm the model's `[SupabaseTable]` name and the selected schema.
- Confirm the role has `SELECT` permission and a matching RLS `SELECT` policy.
- Check every filter sent by the query.
- If an embedded relationship is empty, check its foreign key and the related table's policy.

## Realtime connects but Postgres changes never arrive

- Add the table to the `supabase_realtime` publication.
- Give the current role `SELECT` permission and an RLS policy that can read the changed row.
- Match the subscription schema and table exactly.
- Keep the `SystemMessageReceived` handler visible while diagnosing; Supabase may explain that a
  table, publication, or filter is invalid without closing the whole channel.

The Quickstart's `setup.sql` creates its read policy and publication entry.

## OAuth works in the Editor but not in a build

- Add the exact callback URL to the Supabase Auth redirect allow-list.
- Configure the platform deep link for desktop and mobile builds.
- On WebGL, allow the deployed origin in the project's browser/CORS configuration and serve the
  build over HTTPS.
- Do not enable session persistence as a workaround for a missing OAuth callback. PKCE verifier
  persistence is handled independently.

See [platform notes](platforms.md) for platform-specific setup.

## IL2CPP returns objects with empty properties

Models are populated through reflection. Use the Editor model generator, which adds
`[UnityEngine.Scripting.Preserve]`, or add that attribute to handwritten models when managed
stripping is aggressive.

## Unity freezes during a request

Do not call `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()` on a Supabase task from Unity's
main thread. Use `await` or `AsCoroutine`; the transport needs the main thread to keep advancing.

## Opening an issue

Include the package version, Unity version, target platform, the smallest reproducing code, and the
formatted `SupabaseError`. Sanitize project identifiers and credentials. The repository's support
policy explains what belongs in this project and what belongs in Supabase or Unity support.
