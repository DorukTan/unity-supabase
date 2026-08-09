# Support

This is a community project maintained by one person in their own time. Setting
expectations honestly up front is more useful to you than a promise that gets broken.

## What you can expect

- **GitHub issues are the only support channel.** There is no Discord, no email support, no
  private channel. Everything happens in the open so the next person with your problem can
  find the answer.
- **Best effort, no SLA.** Issues are read. Some are fixed quickly, some sit for a while,
  some are closed as out of scope. Nothing here is guaranteed.
- **Security reports are the exception** and have stated timelines. See [SECURITY.md](SECURITY.md).

## Before opening an issue

Check the [documentation](Packages/com.supabaseunity.client/Documentation~/), particularly
[platform notes](Packages/com.supabaseunity.client/Documentation~/platforms.md) and
[session storage](Packages/com.supabaseunity.client/Documentation~/session-storage.md).
Then search existing issues, including closed ones.

## What belongs here versus elsewhere

**Here:** the package fails to compile, an API behaves differently than documented, a
platform listed as supported does not work, documentation is wrong or missing.

**Not here:** how to write Row Level Security policies, why a PostgREST query returns what it
returns, Supabase pricing or dashboard questions, or general Unity problems. Those belong in
[Supabase's own channels](https://supabase.com/docs) or Unity's. Questions about Supabase
routed here will be closed with a pointer, not because they are unwelcome, but because
answering them badly helps nobody.

## The most valuable thing you can contribute

Platform reports.

The [platform support matrix](README.md#platform-support) reflects one maintainer's testing
on the hardware they own. Linux is marked `Untested` outright, and every other row rests on
a single person's machines, not a device lab.

So: if you ship a build, open a platform report saying what happened, whether it worked or
not. Include your Unity version, the device, and a sanitized log. Reports on older OS
versions, unusual hardware, and aggressive managed stripping settings are the most valuable,
because those are exactly the gaps one developer's testing cannot cover.

That is worth more to this project than most pull requests.
