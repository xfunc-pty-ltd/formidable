# Security Policy

## Reporting a vulnerability

Please report security vulnerabilities privately, not through a public GitHub issue.

Use [GitHub's private vulnerability reporting](https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-writing/privately-reporting-a-security-vulnerability)
for this repository (repo → **Security** tab → **Report a vulnerability**) to open a private
advisory.

<!-- publish-day: verify (private vulnerability reporting enabled, and the mailbox below) -->
If the Security tab offers no such button, private reporting is not enabled for the repository
and there is no advisory for you to open. Email <security@xfunc.com.au> instead, rather than
falling back to a public issue.

Either route, please include:

- A description of the vulnerability and its potential impact.
- Steps to reproduce it, or a minimal repro project/snippet if possible.
- The affected package(s) and version(s) (`Formidable`, `Formidable.Blazor`,
  `Formidable.AspNetCore`).

This is a single-maintainer project without a dedicated security team, so please allow
reasonable time for a response and a fix before any public disclosure. You'll get a reply
on whichever route you used once the report has been triaged.

## Supported versions

Formidable is pre-1.0. Security fixes land on the latest published version; there is no
long-term-support branch for older pre-1.0 releases.
