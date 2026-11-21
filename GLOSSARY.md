# TabTty

TabTty provides tabbed terminal sessions over SSH, Telnet, and serial connections.

## Language

**Connection profile**:
A file-backed configuration for a destination, including its connection method,
authentication choices, terminal settings, and session-logging settings.

**Profile group**:
A set of connection profiles that share a group name.

**Profile registration**:
A local entry for a profile file in TabTty's profile list. An externally moved
file is registered as a new profile rather than continuing its old registration.

**Profile trust**:
Permission remembered for the current user on this PC to connect with a particular
version of a connection profile at its opened location. Both registered and
unregistered profiles can have a trusted version.

**Terminal session**:
A connection attempt and the resulting terminal interaction created from a
connection profile. Its displayed output can remain available after disconnect.

**AI output sharing**:
Permission for a selected external AI application to read a terminal session's
current screen and retained text, independently of permission to send input.

**AI collaboration**:
The period during which a terminal session's output is shared with an AI
counterpart, including both read-only sharing and AI-controlled operation.

**AI sharing counterpart**:
The external AI participant selected to receive a tab's shared output and,
when granted AI control, operate its terminal session. Authorization belongs
to that integration counterpart; it does not necessarily identify an individual
conversation when an AI application shares one integration across chats.

**AI control**:
Permission for an external AI application to send terminal input to a terminal
session while the person uses explicit takeover to resume input. Returning
control to the person does not itself end AI output sharing.

**Connected session**:
A terminal session whose transport is available for interaction. For Telnet,
this does not imply that a remote application has accepted an account login.

**Macro**:
A reusable set of instructions for automating terminal interaction in TabTty.

**Shared macro constant**:
A named, read-only value defined outside the macro source and available to each
macro that explicitly selects its definition file.

**Macro trust decision**:
A user's remembered choice to allow or block a macro on this PC for a specific
combination of macro source contents, ordered invocation arguments, and the
contents of every declared constants file. Identical copies of these files
share the decision when launched with the same arguments.

**Macro run**:
One execution of a macro, distinct from its reusable instructions and the
terminal session it operates.

**Macro pause**:
A temporary suspension of a macro run that preserves its ability to resume.
It is distinct from pausing session recording or ending the run.

**Macro stop**:
The end of a macro run that cannot resume. Starting the macro again creates a
new run.

**Macro receive line**:
A line of filtered received text used for macro matching, which can be complete
or still arriving. It is distinct from an edited screen row or a Text log line.

**Completed-line wait**:
A macro wait that matches text within a receive line after its line ending
has arrived. A match can cover only part of that line.

**Prompt wait**:
A macro wait that can match received text within a line that is still arriving,
without requiring a line ending.

**Macro dialog**:
A request from a macro run for a user's decision, optionally including ordinary
text input. It is distinct from authentication and vault-unlock dialogs.

**Terminal tab**:
A tab representing one terminal session and its retained output through one or
more terminal panes. It belongs to one tab group at a time.

**Disconnected terminal tab**:
A terminal tab with no live connection whose most recent attempt ended through
disconnection, cancellation or failure. A failed attempt is included even when
it never established a connection.

**Explicit closure**:
A user-requested end of a terminal tab, a window, or the eligible disconnected
tabs in a window. Its associated transfer windows close with the targeted
terminal tabs. Automatic tab closure has the same closure behavior but is
triggered by the saved disconnect setting rather than a new user command.

**Automatic tab closure**:
Closing a terminal tab after an ordinary disconnection or a cancelled reconnect
according to the saved automatic-close setting, including its associated
transfer windows. A disconnect that leaves
the terminal tab open also leaves its transfer results available.

**Tab group**:
A region of a TabTty window with its own tab bar and selected tab content.

**Terminal pane**:
A view of the terminal session represented by its containing terminal tab,
with its own position in that session's output.

**Split layout**:
An arrangement of tab groups within a window or terminal panes within a tab.

**Broadcast input**:
The same user input directed to multiple selected terminal sessions.

**Broadcast target**:
A terminal session explicitly selected to receive broadcast input through its
terminal tab. Settings tabs are not broadcast targets.

**Application theme**:
The light or dark appearance of the UI surrounding the terminal, including the
title bar, menus, tabs, status bar, and window frame. It does not change the
terminal's default colors or highlight styles.

**Terminal default colors**:
The fixed black background and white foreground used as the terminal's base
appearance, independently of the application theme. Received terminal color
selections and highlight rules can override that base appearance.

**Highlight rule**:
A user-defined pair of a regular expression and a display style used to emphasize
only the matching part of terminal output. Highlighting changes presentation
without changing the underlying received text or the text recorded in logs.

**Highlight rule set**:
A named, ordered collection of highlight rules that can be selected in a
connection profile and reused across profiles. Its ordering determines precedence
where rules affect the same character.

**Logical output line**:
A line of terminal output that can wrap across multiple terminal rows while
remaining one logical line. It can be incomplete, such as a prompt without a
following newline.

**Terminal row**:
One row of terminal output, on the active screen or retained in scrollback.
One logical output line can occupy several terminal rows through wrapping.

**Scrollback**:
Previously displayed terminal output retained for browsing after it has scrolled
beyond the active screen.

**Retained terminal buffer**:
The normal screen's active rows and retained scrollback together, distinct from
the subset currently visible while the user browses output.

**Receive encoding**:
The character encoding used to interpret incoming terminal data as text.

**Transmit encoding**:
The character encoding used to turn outgoing terminal text into data for the
connection. It can be selected independently of the receive encoding.

**Local echo**:
Terminal display of submitted user input generated by TabTty itself, rather
than returned by the connected peer.

**Session log**:
A recording of terminal output whose sources and representation depend on the
selected recording mode, distinct from a record of every transmission or keystroke.

**Log filename template**:
A text pattern for the base name shared by a recording part's files, using
literal text and optional placeholders such as the connection profile name.
Each recording format supplies its filename suffix.

**Log collision policy**:
The choice to stop recording startup, overwrite, or append when a selected
session log filename resolves to an existing file.

**Host key**:
A public key used by an SSH server to authenticate its identity to the client.
It is distinct from a user's public key used to log in to that server.

**Host-key record**:
A saved association between a connection destination and an approved server
host key, used to recognize it on later connections.

**SSH terminal channel**:
A logical stream within an SSH connection that carries interactive terminal
input and output. Its terminal text encoding is independent of the encoding
rules for SSH protocol messages.

**Transfer endpoint**:
The remote SSH host and account selected by the terminal tab from which a user
opens file transfer. It is the remote side of both uploads and downloads.

**Transfer window**:
The dedicated two-pane view for uploading and downloading files between the
local PC and a transfer endpoint. Its left pane is local and its right pane is
remote.

**Transfer batch**:
One user-started upload or download of the selected files and folder trees to
one destination directory, with a result for each processed item.

**File operation**:
A user-requested upload, download, deletion, rename, or folder creation through
a transfer window, including the outcomes for the affected entries. A transfer
batch is a file operation for uploading or downloading.

**Credential reference**:
The name of a vault entry in the selected vault. The profile field or macro
operation supplies the expected kind. It contains no secret.

**Profile password**:
The saved password selected by a connection profile, independently of whether
a connection attempt uses it or successfully authenticates with it.

**Vault entry**:
A named password or imported private key. Its name is unique within the vault
and identifies it in credential references.

**Credential vault**:
An encrypted file containing saved authentication material that can be moved
between computers and opened with its master password.

**Selected vault**:
The credential vault configured for TabTty connections and credential selection
in connection profiles.

**Master password**:
The secret used to unlock a credential vault, distinct from the credentials
used to authenticate a terminal connection.

**Raw log**:
A byte-for-byte recording of received terminal data, including control bytes
and excluding local echo. It is distinct from network packet capture,
authentication exchanges, and local connection diagnostics.

**Text log**:
A readable recording of received terminal output, with edited line contents on
the normal screen and controls removed or replaced on an alternate screen.
Locally generated echo is excluded.

**Terminal log**:
A recording of received output and enabled local echo as ordered text and
terminal-control operations. It retains editing operations rather than only
final line contents, without promising original-byte fidelity.
