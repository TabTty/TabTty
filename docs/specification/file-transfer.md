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

The tab header command targets the clicked connected SSH terminal tab, including
an inactive tab in another tab group. Open or focus one transfer window for
that tab. Bind it to the tab's current connection generation and show the
authenticated user, host, and port. Shell directory changes, `sudo`, nested SSH,
and profile edits do not retarget it. Tab detachment changes visual ownership
only. A disconnected window remains available for results while its terminal
tab is retained. When the user explicitly opens transfer after reconnect,
replace the old disconnected view with the new generation. No outstanding work
or prior selection is replayed.

Accepted architecture Q10, amended on 2026-11-16, makes automatic disconnected-
tab removal and the user-invoked Close disconnected tabs command close the
transfer windows associated with their targeted tabs, just as explicit
tab/window closure does. Already disconnected targets have no connection
warning. Existing necessary cleanup, reporting, and wait/loss choices still
apply; final exit follows the existing rule. The
[closure design](tab-and-window-closure.md) records these not-yet-implemented
changes and the unchanged actual-cleanup ownership.

Open the local pane at the Windows Documents known folder and obtain the remote
default directory using SFTP REALPATH of `.`. If either fails, show its error and
leave that pane's path editor available. Each pane owns its current directory,
listing revision, and selection. Navigation and Refresh supersede stale listing
results. The local and remote folder controls are keyboard accessible. All
actions, dialogs, and outcomes use the application's language and theme.

Buttons capture the selected source entries and the opposite pane's displayed
directory at invocation. A cross-pane drop captures the highlighted directory,
or the displayed directory for a drop on a file row or empty space. Both use
the same copy operation. Discard a drag if its source selection or target
listing generation is stale. After capture, pane navigation and refresh do not
change the batch. A second batch and file-management mutations in that window
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
new host-trust or vault interactions. Maintain one live SFTP channel per tab;
allow explicit reopen after a channel failure when the SSH connection survives.

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

## Paths, names, and trees

Capture the effective terminal transmit encoding at transfer-window creation.
Encode typed remote paths and generated destination names using that selection,
and decode listing names with it. Apply codec mappings to isolated components,
with fresh state and finalized output for a stateful encoding such as JIS.
Use ASCII `/` only as the SFTP separator between components. Do not apply VT
control handling, stream BOM removal, terminal kana shifts, or replacement
characters to names. Preserve raw bytes from remote listings for requests about
those entries. A non-decodable name can be shown with an escaped byte label;
reject operations that require interpreting it as a usable filename. Reject
generated names that cannot round-trip in the chosen encoding. File data and
line endings are copied byte for byte.

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

## File write and replacement

Read source attributes at open where available. Stream to an exclusively
created temporary sibling using bounded buffers. Finish all writes and close
the staged file successfully before committing the final name. Check expected
length and comparable source attributes again before commit. Detected source
changes fail the file. Preserve source last-modified time where representable,
preferably on the staged file. A timestamp-setting failure is a metadata
warning if the bytes were committed. Keep destination-platform defaults for
permissions and ownership.

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

## File management, deadlines, and limits

Confirm deletion once per selection with side, parent path, selected names,
and inclusion of folder contents. Use Windows recycling where possible. Ask
separately before attempting permanent deletion of local items that cannot be
recycled. Show permanent deletion in the initial remote prompt. On a child
failure stop that top-level tree and continue with other selected entries where
possible. Cancellation stops new destructive requests, keeps completed effects,
and reports failed and unprocessed entries. If an issued mutation's outcome
cannot be verified, report it as unknown and refresh before further mutations.

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
