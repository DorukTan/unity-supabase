# Security model

A Unity player is a public client. Anything included in a build can be extracted.

- Use only `sb_publishable_...` or a legacy `anon` key.
- Never ship `sb_secret_...`, `service_role`, database passwords or management API tokens.
- Enable RLS on every exposed table and test policies as both `anon` and `authenticated`.
- Create Storage policies for every client operation. A public bucket only makes downloads public; writes still need policies.
- Put privileged logic in Edge Functions or another trusted server.
- Session persistence is off by default. Treat persisted refresh tokens as user credentials. For production native builds, provide a custom `ISessionStore` backed by Keychain/Keystore rather than the ordinary file store.
- Do not log access tokens, refresh tokens, authorization callback URLs or password bodies.
- Credentialed SDK requests do not follow HTTP redirects, preventing API keys or user tokens from being forwarded to an unconfigured host. Return a final response or an explicit signed URL from an Edge Function instead.

The Editor build validator rejects unsafe keys in `SupabaseSettings` and scans normal text assets for `sb_secret_` keys and non-anon JWTs. It cannot inspect encrypted/binary assets or credentials assembled/downloaded at runtime, so repository secret scanning and code review remain necessary.

Supabase's [bucket access model](https://supabase.com/docs/guides/storage/buckets/fundamentals) and [Data API setup](https://supabase.com/docs/reference/csharp/installing) are the authoritative policy references.
