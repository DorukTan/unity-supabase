# Security policy

## Supported versions

Only the latest tagged alpha is supported while the public API is still evolving.

## Reporting a vulnerability

Do not open a public issue containing credentials, user data, exploit details or an unpatched vulnerability. Use the repository's [private vulnerability reporting](https://github.com/DorukTan/unity-supabase/security/advisories/new) instead.

Never send a live Supabase secret, service-role key, user JWT or refresh token. If a credential may have been exposed, rotate or revoke it in Supabase immediately before reporting the underlying SDK issue.

Include the Unity version, target platform, package version, minimal reproduction steps and sanitized logs.

## Timelines

This is a single-maintainer project. These are targets, not guarantees:

- Initial acknowledgement: within 7 days.
- Critical vulnerabilities: fix or documented mitigation targeted within 30 days.
- Everything else: addressed in the next release.

If a report goes unanswered for 30 days, you are free to disclose publicly. You will not be
asked to wait longer than that.

## Scope

In scope: anything in this package that leaks credentials, sends them somewhere unintended,
weakens transport security, or causes a client to trust a response it should not.

Out of scope: vulnerabilities in Supabase itself (report those to Supabase), and
misconfigured Row Level Security in your own project. This package cannot compensate for
missing RLS policies, and does not try to.
