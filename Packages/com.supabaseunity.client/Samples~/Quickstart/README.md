# Quickstart sample

This sample reads a small leaderboard and listens for changes to it. It is intentionally a
script instead of a prebuilt scene, so you can see every object added to your project.

## 1. Configure Supabase

1. Open **Tools > Supabase > Setup**.
2. Create or select a `SupabaseSettings` asset.
3. Copy the Project URL and `sb_publishable_...` key from the Supabase Dashboard **Connect**
   dialog.
4. Click **Test Project Connection**.

Never paste an `sb_secret_...` or `service_role` key into Unity.

## 2. Create the sample table

Open the Supabase SQL Editor and run [`setup.sql`](setup.sql). It creates `public.scores`,
enables Row Level Security, grants read access to the client roles, adds a read policy, seeds
three rows, and enables Postgres Changes for the table.

The policy is deliberately public so the unauthenticated sample can read the leaderboard. Use
user-specific policies for real player data.

## 3. Run it in Unity

1. Create an empty GameObject in any scene.
2. Add the `SupabaseQuickstart` component.
3. Assign the settings asset.
4. Press Play.

The Console should show three scores followed by a Realtime subscription message. To trigger a
change, run this in the Supabase SQL Editor:

```sql
update public.scores
set score = score + 1
where player_name = 'Ada';
```

If the query or subscription fails, start with the
[troubleshooting guide](https://github.com/DorukTan/unity-supabase/blob/v0.2.0-rc.1/Packages/com.supabaseunity.client/Documentation~/troubleshooting.md).
Postgres Changes also requires the table to be in the `supabase_realtime` publication; `setup.sql`
handles that when the standard publication exists.
