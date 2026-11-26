# SFTP file transfer contract

The [feature decisions Q1–Q36](../drafts/file-transfer-issue.md) define initial
release behavior. [ADR 0003](../adr/0003-reuse-the-tabs-ssh-connection-for-sftp.md)
defines transport ownership. The limits below are initial engineering guards
to verify during implementation and load testing. See
[implementation status](implementation-status.md#sftp-validation-2026-11-14)
for executed checks and remaining release coverage.

The ongoing [architecture deepening interview](../drafts/architecture-deepening.md)
records accepted restructuring decisions for the file-operation module. The
[consolidated module design](file-operations.md) is accepted through Q11 and
ready for separately authorized implementation.

## Transfer window and operation ownership

Use the same compact row height as connection profile and vault management in
both panes.

The tab header command targets the clicked connected SSH terminal tab, including
an inactive tab in another tab group. Open or focus one transfer window for
that tab. Bind it to the tab's current connection generation and show the
authenticated user, host, and port. Shell directory changes, `sudo`, nested SSH,
and profile edits do not retarget it. Tab detachment changes visual ownership
only. A disconnected window remains available for results while its terminal
tab is retained. When the user explicitly opens transfer after reconnect,
replace the old disconnected view with the new generation. No outstanding work
or prior selection is replayed.

Under architecture Q10, **Close all disconnected tabs** and automatic
disconnected-tab removal close the transfer windows associated with the tabs
they remove, as explicit tab/window closure does. Under the [window contract](persistence-and-windows.md),
the bulk command includes tabs marked with an exclamation mark on yellow after
a failed or timed-out attempt or a communication error that ended an established
connection. These tabs stay open for the user to read the reason. The targets
need no connection-ending warning. Existing cleanup, reporting, and
wait/loss choices still apply. Final exit follows the existing rule. The
[closure design](tab-and-window-closure.md) records these not-yet-implemented
changes and the unchanged actual-cleanup ownership.

Open the local pane at the Windows Documents known folder and obtain the remote
default directory using SFTP REALPATH of `.`. On an ordinary open, list each
directory once and display that result. If either side fails, keep any listing
and selection on the successful side, show the error and leave the failed
pane's path editor available. Each pane owns its current directory,
listing revision, and selection. Navigation and Refresh supersede stale listing
results. The local and remote folder controls are keyboard accessible. All
actions, dialogs, and outcomes use the application's language and theme.

An edited path is a navigation draft. Enter lists that directory; Escape
restores the last listed path. While a pane is loading or its path field
differs from the listed directory, disable its list, file-management actions
and transfers involving it. Failed navigation leaves the draft available for
correction. A background refresh must preserve an outstanding draft. These
rules apply to new operations; a running batch keeps its captured paths.

Buttons, keyboard shortcuts, and drag-and-drop use the same availability rules
for the same action. While copying is unavailable, including during a required
refresh, suppress the copy cursor and target highlight.
Buttons capture the selected source entries and the opposite pane's displayed
directory at invocation. A cross-pane drop captures the highlighted directory,
or the displayed directory for a drop on a file row or empty space. Highlight
only folder rows during the drag. Accept a drop only if copying is still
available and its destination matches the last preview for that drag. Both use
the same copy operation. Discard a drag if its
selection or either captured listing generation is stale. After capture,
navigation and refresh leave the batch's paths intact. A second batch and
file-management mutations in that window
are unavailable while a transfer batch is active. Serialize create, rename,
and delete in that window, with no new batch admitted while a mutation is in
progress. Browsing and transfer never generate terminal input or broadcast
actions.

The owning window's close confirmation lists affected active operations once.
Closing the transfer window itself confirms cancellation if work is active.
Explicit Disconnect or peer loss invalidates the connection generation and
retains results if the terminal tab remains. Removing that tab, including
automatic removal, closes its transfer windows under Q10. A confirmed terminal
tab/window close cancels associated work and closes its transfer windows.
Final application exit follows the existing terminal-window exit rule. Capture
any required close decision before close-induced cancellation of work.
On window closure, use the existing bounded close policy and report any known
unresolved cleanup before dismissal where a path or outcome is available.

## SSH and SFTP channel

After terminal readiness, request a client-initiated SSH `session` channel and
request the `sftp` subsystem with a reply and no PTY. Begin SFTP INIT after the
subsystem succeeds. Offer version 3 and require a version 3 reply. Show channel,
subsystem, or version refusal in the transfer window. Opening another terminal
tab still makes a separate SSH connection. SFTP uses neither a second login nor
new host-trust or vault interactions. Maintain one opening/live/closing SFTP
slot for the tab's current SSH connection generation. Allow explicit reopen
after a channel failure when the SSH connection survives and the old slot can
safely retire, as detailed below.

Keep one transport packet reader and serialized transport writer. Route channel
messages by their SSH channel identifier with independent credit, buffering,
requests, and EOF/CLOSE state. Match SSH replies in order per channel and SFTP
replies by outstanding request ID, allowing legal reply reordering. Retain
closing channel identity until closure is resolved and prevent reuse from
reassociating late data or replies. Late replies after cancellation may only
complete or release their original work. SFTP data and stderr stay outside
terminal decoding, highlighting, and all session recordings. Display bounded,
localized file-transfer outcomes. Treat server-supplied strings as inert text.

Keep socket, encryption, rekey, and liveness handling common to the authenticated
transport. A malformed SFTP frame, unsupported SFTP reply, file I/O failure, or
SFTP-only close stops affected transfer work without ending a healthy terminal.
A transport framing or connection failure ends both. Cancel SFTP work at request
and channel boundaries. Never interrupt serialization of a shared SSH packet
partway through cancellation. Do not wait for filesystem work, UI, or transfer
credit inside the common receive pump. Schedule interactive input and SSH
control ahead of bulk traffic with bounded fair service for the transfer.

### SFTP channel lifetime and ownership

Use the [shared SSH channel module](transports-and-authentication.md#shared-ssh-channel-ownership)
accepted in Q17-Q19. Its named complete operations own channel open, request
replies, notification/send submission, flow control, close and actual cleanup.
SFTP retains subsystem/INIT/version sequencing, framing, request-ID matching,
file-operation meaning and diagnostics. Bind the incremental SFTP consumer and
bounded peer-request rejection policy before channel open. Normal requests and
data require confirmed open; Close is available while opening. The consumer
cannot wait for subsystem success, INIT/version completion or a future callback.

Validate each frame length before allocating its body; preserve the 256 KiB
maximum. Consume a prefix once bounded parser/frame storage actually owns it,
without waiting for the remaining frame, remote reply or local file I/O.
Reserve needed reply/parser capacity during SFTP request admission. Account for
the original chunk while copying, partial/completed frames, pending replies and
buffers transferred to local I/O through their actual lifetimes. Returning SSH
credit does not release that storage. Preserve the 2 MiB per live/closing channel
and 32 MiB application buffer guards and separate request/provider/result limits;
do not hide a frame queue in another quota or wait on the only callback that can
make capacity available.

Send complete frames in order, supplying whole-frame permitted stopping points.
An individual stop cannot interrupt a packet after the shared writer commits
its irreversible cipher/sequence changes. If a frame remains incomplete, finish
already-admitted work safely or stop all later DATA on that channel and close
under the existing cancellation/error policy; never append a new frame to an
abandoned prefix. Close can progress once the current committed packet settles
without waiting for a zero-credit Send to finish its frame. No guarantee to
finish every cancelled frame or retain that channel is added.

Interpret definite byte progress and uncertain send ranges when classifying
file effects. An uncertain final packet may have delivered a complete mutation
request even when the full-frame write did not return success. Preserve possible
UnknownOutcome and the existing refresh/recovery rules; no automatic replay or
assumed non-execution. The shared channel module does not decide file outcomes.

An existing opening/live slot rejects another start. An explicit reopen may
wait for a closing slot within the existing connection-wait budget, with bounded
waiters or a capacity/busy result. Repeated requests do not reset that deadline.
A timed-out/stopped request never opens automatically later. Retire the old slot
only after protocol identity retirement and channel-owned actual release; a
queued/sent CLOSE, freed payload buffer or timeout alone is insufficient.
Cancellation after OPEN commitment retains its original identity. A late
confirmation only permits old-instance cleanup/CLOSE, never subsystem startup;
a late refusal resolves only that old attempt.

If OPEN/CLOSE never resolves, reopen may remain unavailable on that connection.
Keep the healthy terminal running and report pending/busy/timeout truthfully;
do not add closing slots, force terminal disconnect, reuse unresolved IDs or
reconnect/replay automatically. A fresh explicitly reconnected generation has
its own slot, subject to shared capacity and existing file-operation guards.
Old work remains charged to its original owner, and late completions cannot
change the new slot, credit or results.

Channel-owned release does not claim release of separately transferred parser
storage/local file I/O or erase unknown file outcomes. Pending cancelled I/O
still blocks new mutations under [file-operation cancellation](file-operations.md#confirmation-and-cancellation);
a channel reopen or UI refresh does not bypass that guard. Existing window/tab
close confirmations and ownership remain unchanged. Q19 confirms design readiness
for separately requested implementation; the [27-case verification plan](../drafts/whole-codebase-architecture-deepening.md#candidate-5-verification-plan)
is required future work, not executed evidence.

## Paths, names, and trees

Capture the effective terminal transmit encoding at transfer-window creation.
Encode typed remote paths and generated destination names using that selection,
and decode listing names with it. Apply codec mappings to isolated components,
with fresh state and finalized output for a stateful encoding such as JIS.
Use ASCII `/` only as the SFTP separator between components. Do not apply VT
control handling, stream BOM removal, terminal kana shifts, or replacement
characters to names. Preserve raw bytes from remote listings for requests about
those entries. A non-decodable name can be shown with an escaped byte label.
For such a remote entry, disable Rename and ignore F2. Other operations that
require interpreting the name as a usable filename must reject it. Otherwise
eligible operations using the original path bytes remain available. Reject
generated names that cannot round-trip in the chosen encoding. File data and
line endings are copied byte for byte.

Listings and overwrite/deletion confirmations use the same visible labels for
local and remote names. Escape control characters, Unicode format characters,
line separators, unusual whitespace and literal marker openers. An undecodable
component has a distinct byte label. Retain the original path for the action.
Within the 32 MiB per-window listing allowance, limit the materialized rows to
16 MiB, counting each full child path and its decoded and displayed names.

Validate each listed child and newly encoded component before joining it to a
selected parent. Reject empty names, NUL, `/`, and path traversal components in
the wire name. A codec that generates a separator byte inside a component
cannot represent that name in SFTP. Skip the normal `.` and `..` listing rows.
User-typed navigation is a separate operation which may use a parent path. On
Windows reject invalid/reserved names, device and alternate-stream syntax in
child names, separators, trailing dots/spaces, and paths outside the selected
destination. Use filesystem identity and comparison
rules for a local destination where available, including per-directory case
sensitivity. Preserve the actual chosen directory identity during file work.

Plan the bounded set of source and destination names before writing so known
in-batch aliases fail together, including case-equivalent names on a
case-insensitive destination. For a remote server with unknown case or
normalization behavior, reject potentially colliding siblings if their
distinctness cannot be established. Check the server's create/rename result and
never approve replacing a file created by another item of the same batch.
Validation errors fail the affected item or subtree. A selection containing
multiple references to the same entry is deduplicated.

Copy regular files and directories, including empty directories. Skip links,
junctions, and unsupported entry types during copy, including unsafe destination
child paths. Merge directories by name and leave unrelated items in place. For
recursive deletion, enumerate the selected tree, remove children first, and
remove an empty parent last. Remove a known link entry without following it.
Recheck entry types before descending or mutating and leave unclassifiable
entries in place. On Windows use no-follow opens and check resolved directory
identity for app-driven recursion. SFTP v3 operations are path-based and cannot
lock remote path components across requests. Stop the affected subtree when a
change is observed. A remote server controls its filesystem and can change paths
between checks, so these checks do not provide a remote filesystem snapshot.

Create new directories privately, both for folder copies and for New folder.
On Windows, check for persistent ACLs before creating a directory. Supply a
protected DACL at creation that grants only the current user full control,
with inheritance for child files and folders. Verify ownership and the DACL
before copying any children. On upload, request POSIX mode 0700 in the SFTP
MKDIR and fail that subtree if the server refuses. When merging with a folder
that already exists, preserve its permissions. As with file creation, the
remote server enforces the requested access. [Windows directory creation](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createdirectoryw)

## File write and replacement

Read source attributes at open where available. Stream to an exclusively
created temporary sibling using bounded buffers. Finish all writes and close
the staged file successfully before committing the final name. Check expected
length and comparable source attributes again before commit. Detected source
changes fail the file. Preserve source last-modified time where representable,
preferably on the staged file. The local last-access time stays private.
SFTP v3 couples the access and modification fields, so set both to the source
modification time on upload. A timestamp-setting failure is a metadata
warning if the bytes were committed. Create each local download under the
current user's ownership with a protected DACL granting only that user full
control. Before writing received bytes, verify the created file's owner and
DACL and the volume's support for persistent ACLs. Fail the item if the
destination cannot retain those permissions. Keep the protected DACL on
publication, including approved replacements. For uploads, request POSIX mode 0600 (owner read/write)
in the exclusive SFTP OPEN that creates the temporary file. A refusal fails
the upload. Do not retry with omitted permissions or broaden them after
rename. The server enforces effective access, including its ACLs and mapping
on systems without POSIX modes. A server need not report POSIX mode to support
uploads. The server assigns ownership of uploaded files.
[Windows file security and inheritance](https://learn.microsoft.com/en-us/windows/win32/fileio/file-security-and-access-rights).
[SFTP v3 OPEN attributes](https://datatracker.ietf.org/doc/html/draft-ietf-secsh-filexfer-02#section-6.3)

Classify every downloaded regular file as Windows Internet zone 3, including
files from an authenticated SSH server and files reached through directory
copies. Before writing any received content, attach a local `Zone.Identifier`
stream containing only `[ZoneTransfer]` and `ZoneId=3` to the staged file.
Verify that the mark belongs to the staged file and has the expected value
before publishing it. The new name or approved replacement must carry that
object's mark. Fail the item before commit if the destination cannot store
or verify the mark. Leave an existing destination intact on this failure.
The marker records origin for consumers of Windows zone metadata. Virus
scanning and execution decisions belong to Windows and the consuming application.
[Windows zone stream format](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-fscc/6e3f7352-d11c-4d76-8c39-2516a9df36e8)

Use a no-replace move for a new file. On a collision with an ordinary file,
query both sides and show their names, sizes in bytes, and modification times
in Windows local time. Use Unavailable for a missing value and distinguish an
observed absence from a failed status query. Skip leaves the destination in place
and continues the batch. Cancel stops the batch. Before an approved overwrite,
recheck the destination type, comparable metadata, and identity where available.
Ask again for an observed change. If the file disappeared, use no-replace
creation. On SFTP v3, POSIX rename is an unconditional replace, so a separate
metadata check cannot close the interval between check and replacement.
Serialize TabTty's own final local mutations for the same resolved destination,
including writes from different tabs. External writers and remote aliases may
still change the filesystem between checks.

For local overwrites, capture the volume serial number, 128-bit file ID, size
and modification time from the same opened file. Compare them with the
approved snapshot after staging and once more immediately before publication.
If Windows cannot identify an existing destination, leave it in place. The
final Windows rename names its destination by path, so an external change
between that last check and the rename can still take effect.
[Windows file identity](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_info)
and [rename arguments](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_rename_info)

Use a same-directory Windows file replacement operation for approved local
overwrites and advertised `posix-rename@openssh.com` version `1` for approved
remote overwrites. If the side cannot perform a safe replacement, fail the
overwrite with the original destination preserved. Do not fall back to
delete-then-rename, truncating the original, or writing across volumes. Use
standard non-replacing rename for Q28. Report filesystem operation failures
using their actual outcomes and check both paths when completion is ambiguous.

Cancellation or failure before commit closes open handles and attempts to
remove only that operation's temporary file. Record its path if cleanup cannot
be confirmed. A sent commit whose reply is lost has an unknown outcome. Do not
replay it, count it as committed, or delete the final path as cleanup. Results
identify committed, skipped, failed, unfinished, and unknown items. Retry failed
items explicitly from the beginning on the original live generation. Recheck
the current paths, source, and collisions at retry time.

Each admitted copy or retry owns its planning counters and duplicate-result
tracking until that execution ends. Release them on success, cancellation and
failure. Retain the latest bounded results, including unfinished entries and
unresolved temporary paths, and the plans required for eligible retries. A
rejected admission keeps the previous results and retry information. Verify
that interrupted traversal reports every item once, and that starting a retry
with three results and one retryable failure processes only that file.
See [#181](https://github.com/TabTty/TabTty/issues/181).

## File management, deadlines, and limits

Confirm deletion once per selection with side, parent path, selected names,
and inclusion of folder contents. Use Windows recycling where possible. Ask
separately before attempting permanent deletion of local items that cannot be
recycled. Show permanent deletion in the initial remote prompt. On a child
failure stop that top-level tree and continue with other selected entries where
possible. Cancellation stops new destructive requests, keeps completed effects,
and reports failed and unprocessed entries. If an issued mutation's outcome
cannot be verified, report it as unknown. Before accepting another mutation,
require both panes to display current listings from reads started after the
outstanding I/O retired. On reopening the transfer window for the same
connection, the initial pair counts if it meets those conditions and no newer
unknown outcome has occurred. Reads started too early, cancelled, superseded
or failed leave the restriction in effect. Recheck eligibility before
execution. See [#202](https://github.com/TabTty/TabTty/issues/202).

Use the profile connection-wait budget for SFTP startup and periods without
relevant remote reply or forward byte progress. Unrelated terminal traffic and
other SFTP operations do not refresh that timer. Exclude confirmation waits and
time spent waiting solely on local work. Use an independent bounded local I/O
wait with the same configured duration and keep late provider work charged to
its original operation. A progressing transfer has no overall time cap. Drain
or close an SFTP channel whose timed-out requests cannot be safely retired.

| Resource | Initial engineering guard |
| --- | --- |
| Active transfer batches | One per tab, at most four running application-wide. Show a visible capacity status if a new batch cannot start. |
| SFTP buffers | At most 2 MiB per live or closing transfer channel, 32 MiB application-wide. Count receive credit and owned staged/in-flight buffers without double-counting shared ownership. |
| SSH transfer-channel payload | 1 MiB receive window, 32 KiB maximum SSH channel data. Independently validate SFTP frame lengths before allocation, with a 256 KiB maximum frame body. |
| File requests | At most eight outstanding read or write requests of up to 32 KiB per file, and a separate bounded slot for interactive browse/control requests. Limit server replies to the requested range. |
| Listings and tree operations | At most 50,000 entries per listing or operation, maximum depth 128, and bounded encoded paths up to 32 KiB. Charge owned listing/plan/result metadata against 32 MiB per window and 128 MiB app-wide. Report exceeded limits. Preflight recursive deletion before its first app-issued permanent delete. |
| File I/O | At most eight simultaneous SFTP-related local provider calls app-wide, including late cancelled calls until they return. Transfer work never consumes the reserved terminal/log budgets. |

These bounds are subject to adjustment after measurement without changing the
accepted interaction, error semantics, or terminal response targets.

## Acceptance cases

- Use an inactive SSH tab's context menu while another tab is selected. Verify
  the endpoint, localization, keyboard controls, detachment, one window per tab,
  and explicit reopen after reconnect. Test denied subsystem and SSH channel
  limits with a usable terminal.
- Upload and download files and directories including empty folders, same-name
  directory merges, long and non-ASCII names, zero-byte files, and large files.
  Verify button and cross-pane drag destinations, browsing during transfer,
  and preservation of captured paths. Test distinct terminal transmit/receive
  codecs, invalid filename bytes, ambiguous display names, and byte fidelity.
- Test ordinary new-file commit, approved replacement, Skip and Cancel, missing
  conflict metadata, changes before replacement, read-only/no-space failures,
  link/type conflicts, unrepresentable names, and aliased destination names.
  Exercise unavailable rename extensions, late commit replies, stale results,
  and cleanup failure. Test source mutation, no-progress timeout, and retry.
- Test local recycle and confirmed permanent delete, remote nonempty-folder
  deletion with links, child failure, cancellation, permission denial, and
  loss of the mutation reply. Verify affected paths and result categories.
- Exercise an independent OpenSSH v3 server and the scripted SSH peer under
  simultaneous shell I/O, uploads/downloads, rekey, transfer stalls, and
  disconnect. Compare Raw logs to expected terminal bytes and measure the
  existing interactive response targets with transfer load. Validate the
  separate bounds and verify that late cancellations cannot affect a reopened
  channel or a new connection generation.

Protocol and filesystem references are listed in the
[feature decisions](../drafts/file-transfer-issue.md#primary-references).
