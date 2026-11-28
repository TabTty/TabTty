# Specify file transfer from an SSH terminal tab

Status: initial-release scope confirmed. This issue-body draft uses independent
question numbering. Q1–Q36 are resolved. The [transfer contract](../specification/file-transfer.md)
completes the engineering details for implementation.

The transfer contract also records the agreed follow-up for copying and moving
by Paste in either pane, undoing and redoing transfers, and deletion shortcuts.
[Transfer window](../specification/file-transfer.md#transfer-window-and-operation-ownership),
[Undo and Redo](../specification/file-transfer.md#undo-and-redo),
[Delete and Shift+Delete](../specification/file-transfer.md#file-management-deadlines-and-limits)

Architecture Q10, amended on 2026-11-16, changes result-window lifetime:
explicit, bulk, and automatic terminal-tab closure all close their associated
transfer windows under the same cleanup/reporting policy. Disconnect retains
results only while the terminal tab remains open. This supersedes broader
retention readings of Q13/Q34 below; see the not-yet-implemented
[closure design](../specification/tab-and-window-closure.md).

## Resolved scope

- Q1: Include file transfer in the initial release.
- Q2: The user authorized legacy SCP support if it would add little design
  work. Ship SFTP only. Legacy SCP requires a separate wire protocol, remote
  command execution and shell-specific path quoting, and its own failure and
  interoperability cases. These are outside the SFTP design. OpenSSH 9.0 also
  switched its `scp` command to SFTP by default.
- Q3: Open file transfer from the context menu of a connected SSH terminal
  tab's header. The invoking tab determines the transfer endpoint even when
  another tab is active. Show the selected endpoint in the transfer UI.
- Q4: Support upload from the local Windows PC to that endpoint and download
  from that endpoint to the local PC. Server-to-server transfer is deferred.
- Q5: Open an on-demand SFTP subsystem channel on the invoking tab's
  authenticated SSH connection. Report an additional-channel or subsystem
  refusal in the transfer window. The terminal remains usable after an ordinary
  refusal. See [ADR 0003](../adr/0003-reuse-the-tabs-ssh-connection-for-sftp.md).
- Q6 (amended): Use a dedicated two-pane transfer window while leaving the
  terminal interactive. The left pane browses local Windows folders. The right
  pane browses folders at the selected transfer endpoint. Both lists are
  visible together for selecting files, folders, and destinations.
- Q7 (amended): Support selecting multiple regular files and transferring
  folders recursively in both directions in the initial release.
- Q8 (amended): Confirm each same-name file collision with Overwrite, Skip, and
  Cancel. Show the name, size, and last-modified date and time for both the
  incoming item and the existing destination file. Skip leaves that destination
  unchanged and continues to the next item. A collision dialog applies to both
  upload and download.
- Q9: Copy the selected folder by name under the destination directory,
  preserving its hierarchy and empty folders. Merge with an existing same-name
  directory, preserve unrelated destination entries, and apply Q8 to individual
  file conflicts. A file/directory type conflict fails the affected item.
- Q10: Transfer regular files and directories. Skip symbolic links, Windows
  junctions, and other unsupported entry types in the tree. Show the skipped
  entries and reasons in the results.
- Q11: Write each file to a unique temporary file in its destination directory.
  Commit it to the final name after a successful write. If safe replacement is
  unavailable, preserve the existing file and report a failed overwrite. On
  interruption, attempt cleanup and show the path of any temporary file whose
  removal could not be confirmed.
- Q12: Open the local pane at the Windows user's Documents folder and the
  remote pane at the SFTP server's default directory for the authenticated
  account. Let the user navigate the panes independently.
- Q13: Open one transfer window per terminal tab and bind it to the active
  connection instance. Moving the tab between TabTty windows preserves the
  transfer. On disconnect, stop the transfer and keep its results visible while
  the terminal tab remains open (amended architecture Q10 above). The
  user explicitly opens transfer after reconnect. Ask for confirmation before
  closing a transfer window with an active transfer.
- Q14: Show the current item and byte progress during transfer. Report completed,
  skipped, failed, and unfinished items. Continue after an individual item
  failure while the connection and destination remain usable.
- Q15 (amended): Provide Upload and Download buttons for the selection in the
  source pane, using the directory displayed in the other pane when the user
  starts the transfer. Also support mouse drag-and-drop between the two panes.
- Q16 (amended): Preview the destination before release. In the opposite pane,
  a folder row targets that folder and a file row or empty space targets the
  displayed folder. Within the source pane, an unmodified left-button drop on
  another folder moves the selection into that folder. Use the transfer
  contract for modifier keys and the right-button menu.
- Q17: Let the user explicitly retry failed items from byte zero while the
  original connection remains available. After disconnect, reopen transfer and
  select the items again. Partial-file resume is deferred.
- Q18: Preserve the source file's last-modified time where the destination
  supports it. Report a metadata warning when the file contents were saved but
  the timestamp could not be preserved.
- Q19: Allow one active batch per terminal tab and process its files in
  sequence. Batches on different tabs can run concurrently within shared bounds.
  Keep interactive terminal I/O responsive during transfer.
- Q20: Recheck the approved destination immediately before replacing it. If a
  change is detected, obtain a fresh decision with the current file details.
  The check detects observed changes; SFTP v3 has no conditional replace that
  binds a rename to the checked attributes.
- Q21: Fail an item when its name cannot be saved as given on the destination.
  When multiple items in the batch map to one destination name, report all
  colliding items as errors without choosing a winner.
- Q22 (amended): Keep drag-and-drop within one transfer window. Reject drops
  originating outside it and drops leaving it, including between transfer windows.
- Q23 (amended): Provide editable paths, parent-folder navigation, refresh,
  folder creation, deletion, and rename in the transfer window. The parent of
  a local drive root is the drive list. Drive rows only open drive roots.
  Disable the parent button where no parent is available. Permission editing
  is deferred.
- Q24: Cancelling a batch stops the current transfer and all remaining items,
  preserves committed files and created folders, and attempts to remove its
  temporary files. If the connection is lost after sending the final rename
  request and before its reply, show the file's commit outcome as unknown.
- Q25 (amended): Follow RLogin's selection rule. When the transfer window
  opens, capture the invoking terminal session's effective transmit encoding
  for the lifetime of that window. Use it to decode remote directory names
  and encode user-entered remote paths and new remote names. If the session
  uses the same encoding for terminal input and output, its transmit encoding
  resolves to the receive selection under the existing profile rule. There is
  no separate SFTP encoding field in the initial profile or transfer window.
  Treat each path component as an independent string. Preserve the original
  name bytes when referring to a listed remote entry. If a name cannot be
  interpreted or represented for the requested operation, report the error
  without substituting characters. Copy file contents as bytes.
- Q26: Refresh metadata for both files before prompting. Show sizes in bytes
  and last-modified dates and times in the Windows local time zone. Mark any
  missing field as Unavailable and keep the per-file confirmation choices.
- Q27 (amended): Allow confirmed deletion of selected files and folders,
  including nonempty folders and their contents, in both panes. Use the Windows
  Recycle Bin where available for local items. State explicitly that remote
  deletion is permanent.
- Q28: Rename one selected file or folder at a time within its current directory
  on either side. Report a name collision as an error and preserve the existing
  destination entry.
- Q29: Confirm each deletion selection in one prompt. Show the side, parent
  path, and selected names. State that deleting a folder includes its contents.
  If local recycling is unavailable, ask for separate confirmation before
  permanently deleting the affected items.
- Q30: When deleting a folder tree, remove a symbolic link or junction entry
  itself without traversing its target. Leave unclassifiable entries in place
  and report the failure.
- Q31: Support cancellation during deletion and preserve completed effects.
  Report deleted, failed, and unprocessed items. A child failure stops deletion
  within that selected top-level folder. Continue with other selected top-level
  items while their side is usable. Cancellation stops further items.
- Q32: Keep browsing and refresh available during a transfer batch. Continue
  using the source and destination paths captured when the batch starts. Disable
  create, rename, and delete in that transfer window until the batch finishes.
- Q33: Use SFTP version 3 as the initial protocol baseline for browsing,
  transfer, and file management. Remote overwrite requires a supported safe
  replacement operation. Without it, preserve the existing file and explain
  why the overwrite cannot proceed.
- Q34: Include active transfer-window work in the owning terminal tab or
  window's existing close confirmation. On confirmation, cancel the work and
  close the transfer window. Declining the close leaves the terminal and its
  work open. Explicit Disconnect stops the work and keeps its result visible
  if the terminal tab remains open; automatic tab closure follows amended
  architecture Q10 above.
- Q35: Use the profile's connection-wait setting for SFTP startup and for
  waiting for a remote reply or transfer progress. Exclude user confirmation
  time. On expiry, stop the affected work and show a timeout. Keep the terminal
  usable while its SSH transport is healthy. A progressing transfer has no
  total-duration limit.
- Q36: Recheck available reliable source metadata before committing. If it
  reveals a change, fail that file, clean up its temporary copy under Q11,
  and let the user retry the current source under Q17.

Opening transfer from a terminal tab uses the identity of its SSH connection.
For example, if a user opens transfer from the header of tab B while tab A is
active, the transfer endpoint is B's SSH host and account.

## Filename encoding references for Q25

- Tera Term 5.7.1 documents a regular SSH SCP menu. Its SCP dialog converts
  the user-entered remote paths to UTF-8 before passing them to the SCP
  implementation. Its source separately contains an SFTP test handler. The
  SCP dialog is the relevant supported UI behavior for this comparison.
- RLogin 2.31.9 takes the active terminal transmit charset when it opens an
  SFTP channel. It uses that charset in both directions for SFTP filenames,
  including decoding directory entries and encoding requested paths. A new
  server entry defaults to UTF-8. RLogin's terminal mode defaults include
  CP932 for SJIS and EUCJP-MS for EUC.
- The SFTP v3 draft describes filenames as strings and does not prescribe
  UTF-8 for the filename field. OpenSSH's directory listing copies names from
  `readdir` into the SFTP response. An SSH account can therefore have a terminal
  stream in one encoding and filesystem names stored in another. The initial
  design follows RLogin's encoding selection. Reconsider a separate setting
  if a deployment needs both encodings at the same time.

## Existing contracts the transfer must preserve

- Apply the [session generation boundary](../specification/session-and-recording.md#ownership-and-lifecycle)
  to SFTP work and its UI results. A result arriving after disconnect or
  reconnect belongs only to the original connection instance.
- Keep terminal input, receive processing, and recording within their existing
  [resource and response targets](../specification/resources-and-validation.md).
  Bound SFTP frame size, outstanding requests, directory enumeration, buffered
  bytes, and file I/O separately. A slow filesystem or blocked SFTP channel
  must leave the terminal channel able to make progress.
- Verify the shared transport with concurrent terminal traffic, transfer,
  rekey, and cancellation. Use the scripted SSH fixture for fault cases and
  exercise an independent OpenSSH server for interoperability. Check that Raw
  terminal recordings still match terminal payload bytes under transfer load.
- Exercise recursive remote deletion with post-order directory removal, since
  SFTP v3 rejects removal of a nonempty directory. Check cancellation, child
  failures, and link entries without touching their targets. Exercise local
  recycle and confirmed permanent-delete paths separately.
- Verify baseline v3 new-file commit and same-directory rename without replacing
  an existing destination. Verify approved remote overwrite with advertised
  `posix-rename@openssh.com` version `1` and refusal without safe replacement.

## Primary references

- [OpenSSH 9.0 release notes](https://www.openssh.org/txt/release-9.0)
- [OpenBSD scp manual](https://man.openbsd.org/scp#CAVEATS)
- [RFC 4254, channels and SSH subsystem requests](https://www.rfc-editor.org/rfc/rfc4254.html#section-5)
- [OpenBSD sshd_config, MaxSessions](https://man.openbsd.org/sshd_config#MaxSessions)
- [SFTP version 3 protocol draft](https://datatracker.ietf.org/doc/html/draft-ietf-secsh-filexfer-02)
- [OpenSSH SFTP extensions](https://github.com/openssh/openssh-portable/blob/master/PROTOCOL)
- [Windows file naming rules](https://learn.microsoft.com/en-us/windows/win32/fileio/naming-a-file)
- [Windows file operation and Recycle Bin options](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperation-setoperationflags#fofx_recycleondelete-0x00080000)
- [Tera Term 5 File menu](https://teratermproject.github.io/manual/5/en/menu/file.html)
- [Tera Term 5.7.1 SCP dialog](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/ttssh2/ttxssh/ttxssh.c#L3493-L3567)
- [RLogin manual, section 4.9](https://kmiya-culti.github.io/RLogin/index.html#SFTPWIND)
- [RLogin 2.31.9 charset selection when opening SFTP](https://github.com/kmiya-culti/RLogin/blob/66506599f3baa9f1e916b1268a8a659738dbb30a/RLogin/ssh.cpp#L2753-L2759)
- [RLogin 2.31.9 bidirectional filename conversion](https://github.com/kmiya-culti/RLogin/blob/66506599f3baa9f1e916b1268a8a659738dbb30a/RLogin/SFtp.h#L443-L445)
- [RLogin 2.31.9 server entry defaults](https://github.com/kmiya-culti/RLogin/blob/66506599f3baa9f1e916b1268a8a659738dbb30a/RLogin/Data.cpp#L5835-L5844)
- [OpenSSH SFTP directory listing](https://github.com/openssh/openssh-portable/blob/V_10_2_P1/sftp-server.c#L1086-L1107)
