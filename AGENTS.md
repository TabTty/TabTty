# TabTty

## Current work phase

Work on specification and documentation only. Once the specification is settled,
report implementation readiness to the user. Begin implementation or prototyping
only after the user explicitly requests it.

The initial release requires file-backed connection profiles and excludes macros.
Before profile, window/tab lifecycle, or receive-logging design, read
`docs/drafts/connection-profiles-issue.md`. Preserve the future macro design
constraints in `docs/drafts/future-macro-design.md` without implementing a macro
runtime in the initial release.

## Language

Use English by default for repository content, including documentation, code
comments, and issue and pull request text. Communicate with the user in Japanese.
Localized resources and language-specific test data use their target language.

## Product localization

The initial implementation must support both English and Japanese UI text.
Default to "Use system setting". Also offer explicit English and Japanese
selections. The system option resolves the Windows
display-language preferences to a supported language, falling back to English
when no supported language matches. Persist `en`, `ja`, or `system` as the
selected option, rather than only the language resolved from the OS.
Keep user-visible UI text in locale-specific resources, separate from application
logic, so additional languages can be added without duplicating the UI.
Keep internal identifiers independent of translated display text.

## Product theme

Offer "Use system setting", "Light", and "Dark". Default to the system option.
Use the same system-option label in the language and theme selectors.
In system mode, follow the Windows app light/dark preference and apply changes
while the application is running. Explicit Light or Dark selections override
the system preference. Persist `system`, `light`, or `dark` as the selected mode,
rather than only the theme resolved from the OS.

Apply the theme to the surrounding application UI, including the title bar,
menu bar, tab bar, status bar, and window frame. The terminal's default colors
are always a black background and white foreground; these defaults are fixed
and not user-configurable. Highlight rules use one style across Light and Dark,
without theme-specific variants.
Honor received ANSI foreground and background color selections. Apply highlight
rules over those terminal attributes. Resetting a foreground or background color
restores its corresponding fixed default.

## Dependencies

Directly adopted libraries must, by default, be official Microsoft or OpenSSL
offerings. Required dependencies of those offerings are permitted, including
third-party-origin components. Do not interpret this as requiring all bundled
source code to originate from Microsoft or OpenSSL.

For encrypted OpenSSH private-key files, permit the OpenSSH/OpenBSD
`bcrypt_pbkdf` implementation and its required Blowfish/helper source as a
scoped exception. Keep reuse limited to this key-file support.

Implement the SSH client protocol and session layer in TabTty, using OpenSSL
for cryptographic operations. Before SSH design or changes, read
`docs/adr/0001-own-ssh-client-with-openssl.md` for the accepted boundary.

## Authentication and secrets

Before designing or changing authentication, credential storage, macro access to
secrets, or diagnostic logging, read `docs/drafts/authentication-issue.md`.
Before vault storage or unlocking changes, also read
`docs/drafts/credential-vault-issue.md`. The selected vault must be portable
between PCs with a master password. All connection profiles and windows share
its unlock state until the user locks it or the application exits.
Use current Microsoft, OpenSSH, and OpenSSL guidance and applicable IETF and
OWASP recommendations. Recheck affected guidance when these behaviors change
or relevant dependencies change, at implementation start, and before release;
document material deviations and their rationale.

## Agent skills

### Issue tracker

Track issues and specifications in GitHub Issues.
Before working with issues, read `docs/agents/issue-tracker.md`.

### Triage labels

Use the five default triage labels.
Before classifying issues, read `docs/agents/triage-labels.md`.

### Domain docs

Use a single-context layout.
Before exploring or designing code, read `docs/agents/domain.md`.
