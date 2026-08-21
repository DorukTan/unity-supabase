create table public.unity_acceptance_scores (
    id uuid primary key default gen_random_uuid(),
    user_id uuid not null default auth.uid() references auth.users (id) on delete cascade,
    run_id text not null,
    score integer not null,
    created_at timestamptz not null default now(),
    unique (user_id, run_id)
);

alter table public.unity_acceptance_scores enable row level security;

grant select, insert, update, delete on table public.unity_acceptance_scores to authenticated;

create policy "Users can read their acceptance rows"
on public.unity_acceptance_scores for select
to authenticated
using (user_id = auth.uid());

create policy "Users can create their acceptance rows"
on public.unity_acceptance_scores for insert
to authenticated
with check (user_id = auth.uid());

create policy "Users can update their acceptance rows"
on public.unity_acceptance_scores for update
to authenticated
using (user_id = auth.uid())
with check (user_id = auth.uid());

create policy "Users can delete their acceptance rows"
on public.unity_acceptance_scores for delete
to authenticated
using (user_id = auth.uid());

alter publication supabase_realtime add table public.unity_acceptance_scores;

create policy "Users can upload their acceptance objects"
on storage.objects for insert
to authenticated
with check (
    bucket_id = 'unity-acceptance'
    and (storage.foldername(name))[1] = auth.uid()::text
);

create policy "Users can read their acceptance objects"
on storage.objects for select
to authenticated
using (
    bucket_id = 'unity-acceptance'
    and (storage.foldername(name))[1] = auth.uid()::text
);

create policy "Users can update their acceptance objects"
on storage.objects for update
to authenticated
using (
    bucket_id = 'unity-acceptance'
    and (storage.foldername(name))[1] = auth.uid()::text
)
with check (
    bucket_id = 'unity-acceptance'
    and (storage.foldername(name))[1] = auth.uid()::text
);

create policy "Users can delete their acceptance objects"
on storage.objects for delete
to authenticated
using (
    bucket_id = 'unity-acceptance'
    and (storage.foldername(name))[1] = auth.uid()::text
);
