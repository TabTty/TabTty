# Connection profile registration and trust

Specification for implementation, 2026-11-19. [日本語](profile-trust-ja-jp.md).
This replaces the earlier Q112 decision to trust every opened profile. It
applies to SSH, Telnet and serial profiles. The macro rules apply in builds
with macro support.

## Connection decisions

Ask for profile trust only when a connection is requested. Read one bounded,
validated snapshot and check the current registration and trust for its opened
location. Apply the following table to each file independently.

| Request | Registration | Trust for this version | Action |
| --- | --- | --- | --- |
| Manual open or reconnect | Unregistered | Either | **Connect once**, **Register and connect**, **Cancel** |
| Manual open or reconnect | Registered | Trusted | Connect directly |
| Manual open or reconnect | Registered | Untrusted | **Connect once**, **Trust this version and connect**, **Cancel** |
| Macro `Connect` | Either | Trusted | Connect directly. Preserve registration as it is. |
| Macro `Connect` | Either | Untrusted | **Connect once**, **Trust this version and connect**, **Cancel**. Preserve registration as it is. |

Manual open includes Explorer, CLI file activation, the in-app file picker
and the registered list. Re-evaluate the file on each reconnect. The default
button is **Cancel**. Cancelling stops only this request. **Connect once**
authorizes this attempt and preserves any existing registration and trust.
**Register and connect** completes registration, automatically trusts the
loaded version and starts the connection with that snapshot. A subsequent
network or authentication failure leaves the completed registration and trust
in place. **Trust this version and connect** remembers the version for this
location and starts this attempt. It never creates a registration.

Macro launch approval covers the source, ordered arguments and contents of
every declared constants file. Evaluate
profile trust separately when its actual `Connect` path has been resolved,
including after a change of base directory. A trust record created there
is shared with other approved callers. Opening that unregistered file
manually still presents the registration choice.

## Registration and editing

- Successful new registration automatically trusts the exact version being
  registered. This includes **Register** in the profile list, **Register and
  connect**, and a verified new-profile or duplicate save that creates a
  registration. The standalone **Register** operation completes without
  starting a connection. Identify the file and explain the trust effect in
  the registering action itself. There is no second trust confirmation.
  Receiving a file activation or reserving its tab does not register it.
- The main window's editor may advance trust on a verified save of a profile
  whose exact starting version was trusted. For an existing unreviewed or
  externally changed version, ordinary Save writes the edits. Trust is
  decided at the next connection. An editor opened by Explorer or external
  activation can save normally and grants no trust through that save.
  Reactivating an existing editor from another route does not elevate its
  save authority. Do not prompt about profile trust on edit, save or close.
- Registration is a transition from unregistered to registered. Looking up
  an existing entry, refreshing its display name or usage, opening the list
  and loading saved registrations do not renew trust. Preserve existing
  registrations on upgrade and treat versions without trust records as
  untrusted.
- Unregister removes the entry and all profile trust for its opened location,
  including trust remembered through a macro. The same rule applies when a
  completed deletion removes the registration. Other file locations are
  independent. Explicit re-registration trusts the version registered then.

## What a decision covers

Scope a trusted version to the current Windows user on this PC, the normalized
opened location, SHA-256 of the exact file bytes and the version of the rules
that interpret security-relevant settings. Use the existing Windows location
comparison rules and profile-relative log base. Keep trust outside the profile
and vault. Trusting a new version replaces the location's previous approval.
A copy at a different location requires its own decision.
[Profile location rules](persistence-and-windows.md)

Before asking, show the file location, protocol, destination and port or serial
port, SSH account and authentication methods, any credential references, and
enabled recording formats and resolved log destination. Identify network shares
and highlight changed settings when previous details are available. Escape
display controls and keep secret values out of the review. For a macro request,
also identify the macro. Reading an externally supplied network file can itself
contact a share. Obtain permission to read that source before making such a
request. That permission covers reading the file only.

For an SSH profile with a saved credential, use the expected account from the
captured profile in this review. After host key verification and before the
first SSH user authentication request, compare it with the account of each
available saved credential. Any allowed continuation without an unavailable
credential keeps the approved account from the captured profile.
[Saved SSH credentials](transports-and-authentication.md#authentication-and-user-facing-messages)

Use the approved snapshot for recording, macro profile-password selection and
the transport. Wait for the connection decision before accessing the log
destination, resolving the terminal host, opening a port or using a credential.
Check the effective log destination when admitting the connection. If a mapping
later redirects recording elsewhere, hold recording for that destination.
Recording operations cannot grant or renew profile trust. Keep SSH host-key
checks and vault unlock in their normal order. Other sessions continue to accept input during review.
The existing modeless destination-change notice after a successful reconnect
has no effect on these decisions or on broadcast selection.

## Reset and pending operations

Settings offers two independently confirmed commands. Each confirmation names
the records it will remove and defaults to **Cancel**.

| Command | Records removed | Next use |
| --- | --- | --- |
| Reset all macro trust | Every remembered macro allow and block decision for this user on this PC | Apply the initial macro review on the next launch |
| Reset all connection profile trust | All trusted profile versions for this user on this PC, including unregistered locations | Apply the connection table on the next attempt. Keep the profile list, favorites and usage history. |

Neither command removes the other kind of trust, SSH host keys or vault data.
Already admitted connections and macro runs continue. A running macro that
has yet to call `Connect` uses the profile trust in force when it does so.

Order registration changes, trust changes and admission across windows.
On unregister or reset, invalidate pending reviews and save-time trust updates
affected by that change. A stale answer or late save completion cannot restore
an erased decision or start a connection on the strength of that decision.
Re-evaluate a still-requested connection using current state. Independent
requests and the other type of trust keep their decisions.

Make registration with automatic trust, and unregistration with trust removal,
consistent across failures and restarts. A failed or uncertain write cannot
authorize a connection or report a successful reset. Report the outcome and
provide a retry. Recovery must not resurrect erased trust from an old backup.

## Acceptance cases

1. Choose **Connect once** on an unregistered manual open. The next manual
   open offers all three choices again. Choose **Register and connect** and
   reconnect with the same bytes. Only the first of these two attempts prompts.
2. Trust an unregistered version from a macro. A second approved macro can use
   that version directly. Explorer still offers **Register and connect**.
3. Register in the list, then edit the file externally. The edit causes no
   trust prompt. Before the next connection or logging starts, review the
   new version using the registered-profile choices.
4. Unregister a profile while its tab remains connected. That tab continues
   to work. Its next manual reconnect offers the unregistered choices. A macro
   now needs a fresh decision for the same location and version.
5. Reset profile trust with one registered and one unregistered trusted
   profile. After restart both require the applicable decision. The registered
   entry still appears in the list and remembered macro decisions still apply.
6. Reset macro trust with both allow and block records present. Both macros
   return to initial review on their next launch. Previously trusted profile
   versions still pass their profile checks.
7. Reset while an affected confirmation or editor save is pending. A later
   reply or save completion cannot re-create the previous trust. A newly
   initiated registration at that location trusts its current version.
