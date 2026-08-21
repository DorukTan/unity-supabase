# Live acceptance testing

The normal EditMode suite is credential-free and remains the release gate. This optional PlayMode
test checks the complete client against a real Supabase stack: Auth, PostgREST, Realtime, Storage,
and an Edge Function.

## Local stack

Install a Docker-compatible container runtime, close the Unity Editor, then run:

```powershell
pwsh .github/scripts/run_live_acceptance.ps1 -StartLocalStack
```

The script uses the pinned Supabase CLI through `npx`, reads the local URL and anon key from
`supabase status`, and passes them to Unity without printing credentials. The committed migration,
seed, Storage policies, and `unity-acceptance` function create an isolated test surface.

When the local migration changes and the local data is disposable, rebuild only the local database:

```powershell
pwsh .github/scripts/run_live_acceptance.ps1 -ResetLocalDatabase
```

`-ResetLocalDatabase` destroys the local development database. It never targets a linked project.

## Isolated hosted project

Use a throwaway development project, never production. Apply the files under `supabase/`, then set:

```powershell
$env:SUPABASE_TEST_URL = "https://your-test-project.supabase.co"
$env:SUPABASE_TEST_PUBLISHABLE_KEY = "your publishable or legacy anon key"
$env:SUPABASE_TEST_EMAIL = "unity-acceptance@example.test"
$env:SUPABASE_TEST_PASSWORD = "a test-only password"
pwsh .github/scripts/run_live_acceptance.ps1
```

Do not provide a secret or `service_role` key. The client rejects elevated credentials, and the
acceptance schema is deliberately protected by Row Level Security and user-scoped Storage policies.
