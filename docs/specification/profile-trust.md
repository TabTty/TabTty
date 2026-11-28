# Connection profile registration and trust

Specification for implementation, 2026-11-19. [日本語](profile-trust-ja-jp.md).
This replaces the earlier Q112 decision to trust every opened profile. It
applies to SSH, Telnet and serial profiles. The macro rules apply in builds
with macro support.

## Connection decisions

Evaluate profile trust when a connection is requested. Read one bounded,
validated snapshot and check the current registration and trust for its opened
location. Apply the following table to each file independently.

| Request | Registration | Trust for this version | Action |
| --- | --- | --- | --- |
| Manual open or reconnect requiring review | Unregistered | Either | **Connect once**, **Import and connect**, **Cancel** |
| Manual open or reconnect | Registered | Trusted | Connect directly |
| Manual open or reconnect | Registered | Untrusted | **Connect once**, **Trust this version and connect**, **Cancel** |
| Macro `Connect` | Either | Trusted | Connect directly. Preserve registration as it is. |
| Macro `Connect` | Either | Untrusted | **Connect once**, **Trust this version and connect**, **Cancel**. Preserve registration as it is. |

Manual open includes Explorer, CLI file activation, the in-app file picker
and the registered list. Re-evaluate the file on each reconnect. The default
button is **Cancel**. Cancelling stops only this request. **Connect once**
authorizes this attempt and preserves any existing registration and trust.

A tab opened with **Connect once** from an unregistered file can reconnect to
that same file without a prompt while the file is still unregistered and
matches the last version approved in that tab. Treat this as **Connect once**
for the new attempt. A changed version requires all three unregistered
choices. If the location has since become registered, apply the registered
rows. A tab opened using a registration that has since been removed requires
the unregistered choices on reconnect. The implicit decision belongs to the
tab and grants no remembered trust or registration.

**Import and connect** completes [profile import](persistence-and-windows.md#importing-profile-files),
including trust for the verified destination version, then starts the
connection with that snapshot. The tab uses the registered destination for
subsequent reconnects. **Trust this version and connect** remembers the
version for the opened location and starts this attempt without registering it.

Macro launch approval covers the source, ordered arguments and contents of
every declared constants file. Evaluate
profile trust separately when its actual `Connect` path has been resolved,
including after a change of base directory. A trust record created there
is shared with other approved callers. Opening that unregistered file
manually still presents the registration choice.

## Registration and editing

- Successful new registration automatically trusts the exact version being
  registered. This includes **Import file** in the profile manager,
  **Import and connect**, and a verified new-profile or duplicate save that
  creates a registration. **Import file** completes without starting a
  connection. A nonblank post-connection macro requires the registration
  review described below. The connection review covers **Import and connect**.
  Receiving a file activation or reserving its tab does not register it.
- The main window's editor may advance trust on a verified save of a profile
  whose exact starting version was trusted. For an existing unreviewed or
  externally changed version, ordinary Save writes the edits. Trust is
  decided at the next connection. An editor opened by Explorer or external
  activation can save normally and grants no trust through that save.
  Reactivating an existing editor from another route does not elevate its
  save authority. Editing, saving or closing an existing profile needs no
  separate trust review.
- Registration is a transition from unregistered to registered. Looking up
  an existing entry, refreshing its display name or usage, opening the list
  and loading saved registrations do not renew trust. Versions without trust
  records are untrusted.
- Unregister removes the entry and all profile trust for its opened location,
  including trust remembered through a macro. The same rule applies when a
  completed deletion removes the registration. Other file locations are
  independent. Explicit re-registration trusts the version registered then.

## What a decision covers

A nonblank post-connection macro is executable profile content. A connection
review and an import or new-save registration review show that it runs
automatically and expose the complete body as inert text with display controls
escaped. Import itself performs no run. Use the exact approved snapshot.
Scope trust to the interpretation that includes the executable body, so a
trust record for the same bytes interpreted as inert cannot authorize execution.
The ordinary verified-save and trust-location rules apply.

Scope a trusted version to the current Windows user on this PC, the normalized
opened location, SHA-256 of the exact file bytes and the version of the rules
that interpret security-relevant settings. Use the existing Windows location
comparison rules. Keep trust outside the profile
and vault. Trusting a new version replaces the location's previous approval.
A copy at a different location requires its own decision.
[Profile location rules](persistence-and-windows.md)

After upgrade, a prior approval that included the resolved recording
destination is reviewed once using the file-only trust check. Recording
preparation establishes its own drive mapping history.

Before asking, show the file location, protocol, destination and port or serial
port, SSH account and authentication methods, any credential references, and
enabled recording formats. Highlight changed settings when previous details are available. Escape
display controls and keep secret values out of the review. For a macro request,
also identify the macro. Reading an externally supplied network file can itself
contact a share. Obtain permission to read that source before making such a
request. That permission covers reading the file only.

Display the SSH username from the captured profile during review and use that
same value throughout authentication, including when an unavailable saved
credential causes a method fallback.
[Saved SSH credentials](transports-and-authentication.md#authentication-and-user-facing-messages)

Use the approved snapshot for recording, macro profile-password selection and
the transport. Wait for the connection decision before accessing the log
destination, resolving the terminal host, opening a port or using a credential.
Resolve and check the effective log destination during recording preparation.
Ask before using a changed drive mapping, and record that approval separately
from profile trust. Use the captured mapping throughout the recording session.
See [the recording contract](../drafts/receive-logging-issue.md#log-filename-template-redesign).
Keep SSH host-key
checks and vault unlock in their normal order. For an inactive tab that needs
a trust decision, present the review dialog identifying the tab and destination
as soon as the decision is needed. Keep keystrokes directed to the user's
current input target until the user focuses the dialog. Only the affected
operation waits. After a successful reconnect to a changed destination, follow
the [notification policy](notification-policy.md) for the tab notice and the
additional modeless notice when the tab is inactive. Acknowledging either
preserves the completed trust decisions and broadcast selection.

## Trust removal and pending operations

The **Maintenance** tab offers **Remove individually** and **Remove all** for
each trust category. Each bulk confirmation names the records it will remove
and defaults to **Cancel**. For presentation and the separate protected
stores, see [Settings maintenance](settings-maintenance.md).

| Category | Individual or bulk scope | Next use |
| --- | --- | --- |
| Macro trust | One [v1 macro allow or block record](../drafts/macro-specification.md#macro-trust-record-v1), or all such records for this user on this PC | Apply the initial macro review on the next affected launch |
| Connection profile trust | The remembered version for one location, or all versions for this user on this PC. Include unregistered locations. | Apply the connection table on the next affected attempt. Keep the profile list, favorites and usage history. |

Neither command removes the other kind of trust, SSH host keys or vault data.
Already admitted connections and macro runs continue. A running macro that
has yet to call `Connect` uses the profile trust in force when it does so.

Order registration changes, trust changes and admission across windows.
On unregister or individual or bulk trust removal, invalidate pending reviews and save-time trust updates
affected by that change. A stale answer or late save completion cannot restore
an erased decision or start a connection on the strength of that decision.
Re-evaluate a still-requested connection using current state. Independent
requests and the other type of trust keep their decisions.

Make registration with automatic trust, and unregistration with trust removal,
consistent across failures and restarts. For a new registration, verify the
catalog and trust changes before admitting the requested connection. If either
write fails, verify and undo any changes committed by that registration. Before
changing either store, retain enough local operation evidence to determine or
compensate a partial outcome after a restart. That evidence grants no authority
and stays outside the ZIP. Verify actual effects before undoing an uncertain
write. Preserve preexisting records and later independent changes.

A failed or uncertain action cannot authorize a connection or report success.
If undoing an incomplete registration also fails, show the confirmed effects,
the rollback failure and the remaining changes. Keep the affected new trust
unusable while the operation remains unresolved. Unregister and trust removal
must invalidate stale work. Recovery cannot resurrect trust removed by an
independent action or load it from an old backup.

For **Unregister**, verify that the trust for the location is absent before
removing its catalog entry. If removal of an existing trust record succeeds and
catalog removal then fails, keep the trust revoked. Show **Trust was removed.
Could not remove the registration from the list.** and offer **Retry unregister**.
Recheck the current registration and trust before retrying so a stale request
does not act on later changes. The revoked decision stays revoked across restart.

## Acceptance cases

1. Choose **Connect once** on an unregistered manual open. Reconnect in the
   same tab with unchanged bytes and verify that the decision is implicit.
   A fresh manual open or a reconnect after an external edit offers all three
   choices. Choose **Import and connect** and verify that reconnect uses the
   registered destination without a prompt for the same bytes.
2. Trust an unregistered version from a macro. A second approved macro can use
   that version directly. Explorer still offers **Import and connect**.
3. Import from the manager, then edit the registered file externally. The edit causes no
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
8. Import an external file whose original name and first candidate are occupied,
   including by an unregistered file. Verify that the next candidate receives
   the exact bytes, that the source is unchanged, and that no connection starts
   from **Import file**. Import a file already directly in the configured
   folder and verify that it is registered in place.
9. Fail import before registration and trust are verified. Verify that no
   connection starts and that any surviving copy is reported. Fail authentication
   after a completed import and verify that the registered copy remains.
10. Import and connect with a relative log directory and a colliding file name.
    Verify that the list, tab and log filename use the destination stem and that
    logs resolve from the import folder. Switch folders and back. Verify that
    each folder shows its own registrations and that favorites and usage history
    return with their original folder.
11. During unregistration, fail catalog removal after trust removal succeeds.
    Verify the report shows the two outcomes. After restart, verify that the
    profile still requires the ordinary trust review and that retrying can remove
    the remaining registration.
