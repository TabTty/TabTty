# TabTty

TabTty runs on Windows 11 x64. Extract the whole folder and run `TabTty.exe`.
Keep its .NET runtime, native libraries and license files together.

To register TabTty file types for your Windows account, open **Settings →
Maintenance** and select **Register TabTty file types with Windows**.
This adds TabTty to **Open with** for `.ttty` (connection profiles), `.tttym`
(macros), and `.tttykey` (credential vaults). It also registers Explorer icons
for those types and for `.tttylog` (raw and terminal session logs). Keep the
Assets folder next to the executable.

To uninstall, close all TabTty windows. Open PowerShell in the extracted
folder using the same Windows account (no administrator rights needed) and run:

```powershell
Unblock-File -LiteralPath .\Uninstall-TabTty.ps1
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\Uninstall-TabTty.ps1
```

Use the script from a ZIP you trust. Add `-WhatIf` to the second command to
preview the registry changes. The script removes TabTty's file type
registrations for the current account, shared by all extracted copies.
Windows default app choices are left alone; change them in **Settings →
Apps → Default apps** if needed. The script keeps application files, settings,
profiles, vaults, macros and logs. After it succeeds, you can delete the
extracted application files manually, keeping any data you saved there.
To register the file types again, use the same button in Settings.

Use the menu button at the right of the tab bar to access File, Edit, View,
Window, Connection and Tools. Drag the empty area of the tab bar to move the
window. Its right edge contains the minimize, maximize and close buttons.
Settings, registered profiles, the credential vault, SSH
host keys and highlight sets open as tabs. Close a tab with its × button.
Use the Tab list button at the left of the tab bar to jump to any tab in that
tab group. A faded edge indicates tabs beyond that edge. Scroll the mouse
wheel over the tab strip to select the next tab (down) or previous tab (up).
Selection wraps at either end of the list.
Closing an editor tab asks before discarding unsaved changes. Save applies the
changes and keeps the tab open. Discard changes restores the last saved values,
or the initial values for a new profile, and keeps the tab open.
TabTty dialogs use the application theme. Windows file pickers use the
appearance supplied by Windows.

## Connections

Open the + menu next to the tabs and select a registered profile by name to
connect. The same menu offers New profile and Open profile. Select Telnet, SSH
or COM in the profile editor and save a `.ttty` file. New, edited, and duplicated
profiles open in a separate window. Use the category list on the left to move
between sections in the profile editor and Settings. Save keeps the editor open,
and closing it asks before discarding any unsaved changes.
Select that saved file to connect. The profile catalog supports registration,
favorites, search, editing and duplication. In Windows, associate `.ttty`
and `.tttykey` with TabTty to use these File Explorer commands.

- For `.ttty`, choose **Open**, double-click, or press Enter to register the
  profile and connect. A multi-selection starts one connection tab per file
  in the most recently active terminal window.
- Choose **Edit** for `.ttty` to open one editor window per selected file.
  Save writes to the opened file. Look under **Show more options** for Edit.
- For `.tttykey`, choose **Open** or **Edit**, or double-click a single file
  to open its vault editor. The selected vault uses the same unlock state in
  the standalone window, the TabTty management tab, and connections.

For a profile received from someone else, choose **Edit** to inspect its
destination, credential references and recording settings before connecting.
When recording is enabled, TabTty opens the log files before connecting to the
terminal. An absolute UNC log path, or a relative log path in a profile opened
from a network share, can cause Windows to authenticate to that share and save
the terminal output there.

Profile changes take effect on the next connection. Font and retained-row defaults
also apply on the next connection. Language, application theme, bell and saved
highlight rules apply after saving. The terminal uses fixed black and white
defaults while honoring received ANSI colors. Language and theme follow Windows
settings by default. The UI falls back to English if the Windows display
language preferences contain no supported language.

Tabs can move between windows or detach by dragging outside a window. Closing an
active connection asks for confirmation. TabTty closes disconnected tabs by
default. To keep their output available after disconnect, turn off Automatically
close disconnected tabs in Settings. A broken-link icon beside the connection
name marks a retained disconnected tab. To close the disconnected
tabs already in the current window, right-click its tab bar and choose Close all
disconnected tabs. Closing the last tab leaves the window open for new tabs.
Moving the last tab to another window closes the source window.

## Macros

In a build that includes macros, choose **File → Run macro** and select a trusted
`.tttym` file. Add each ordinary argument as a separate entry. You can also run
`TabTty.exe --macro "C:\ops\check.tttym" -- "argument with spaces"` or associate
`.tttym` with TabTty through Windows Open With. File association supplies no
arguments. For a new combination of file contents and arguments, TabTty shows
the path, source digest and arguments before execution. A macro can send
terminal commands and submit saved passwords. **Cancel** is the default.
You can run it once or remember an allow or block for that exact combination.
An identical copy with the same arguments uses the same decision. To forget
a decision, open **Settings → Maintenance → Manage macro decisions**. Windows
Credential Manager stores decisions for your Windows account on this PC.
External requests that could read a remote source require separate read
permission. An elevated run also requires explicit approval.

Start the source with `#tttym 1`. A macro opens a new tab and can connect using
one saved profile. Relative profile paths start in the macro's directory.
For example, this script opens a profile and waits for an exact prompt substring:

```text
#tttym 1
Connect(@"profiles\server.ttty");
WaitPrompt(Literal("$ "));
```

Use the macro controls to pause, resume, or stop. A stopped macro leaves a
healthy connection available for manual input. After a recording failure,
retry recording and then explicitly resume the macro. If receive history was
lost, the tab offers a choice to continue or stop. Password operations resolve
vault references at the time of use. To obtain one, select an entry and use
**Copy macro reference** in vault management. See the
[macro language reference](drafts/macro-language.md).

## Broadcast input

Right-click a terminal tab and choose **Broadcast Input → Start Broadcast**, or
use the same command under **Connection**. Check each terminal tab that should
receive input. The status shows the effective scope and how many selected
connections are ready. A terminal tab can supply input even when its own check
is cleared. Enter, terminal keys, committed text, paste and remote mouse
reports use each selected session's settings. Local scroll, selection and search
operate on the current view. Settings and profile editors have no target checkbox.

The first Start selects the existing terminal tabs in scope. Use **Select All
Targets** or **Clear All Targets** to change the selection. Stopping and restarting
with the same setting keeps the selection. Newly opened terminal tabs start checked,
and moving a tab to another window checks it for subsequent operations.
Reconnecting a tab preserves your selection. If the destination changes, a topmost
notification shows the previous and current destinations. You can keep typing and
broadcasting while it is open.
Hover over a recipient checkbox to see its transport and destination.

To broadcast across windows, enable **Allow broadcast input across windows**
in **Settings → Tab behavior**. The next Start applies the new scope and resets
selection to all connection tabs in that scope. An active broadcast continues
using its current scope until stopped. While the all-window preference is saved,
**Stop Broadcast** ends broadcasts in every window.

A multiline paste asks once for confirmation and lists each recipient's destination and tab name. During
paste or a remote mouse drag, the recipients stay fixed. **Stop Paste** ends the
remaining paste across all recipients. If sending fails, check the status for
each recipient's outcome before trying again.

During a window-local paste or remote drag, tabs can move between groups in that
window. Wait for the operation to finish before moving a tab across that window's
boundary. All-window broadcast allows moves between windows during the operation.

## Tab groups and terminal panes

Open **Window → Tab Groups** to split the current group left/right or top/bottom.
Each group has its own tab bar. Drag an existing tab to a group's tab bar, or
use **Window → Move Tab** to choose a destination. A group's + menu opens a
connection from a saved profile. **Remove Group and Move Tabs To** lets you pick
the destination for the removed group's tabs.

Open **Window → Terminal Panes** or a terminal pane's context menu to split the
selected terminal view. Panes show the same connection. Each has its own scroll
position, selection and current search result. The search query, case setting,
and zoom are shared. Removing a pane keeps the session connected.

Drag a divider to resize adjacent regions. You can also focus a divider and use
the arrow keys. The group and pane menus provide commands to move focus between
regions. If the window becomes too small for an existing split, scroll the layout
to reach the other regions. Each application run starts with one group and no
inner splits.

## Credentials and host keys

The shared `.tttykey` vault holds passwords and imported SSH private keys.
Set its path in Settings, then choose Credential vault → Unlock vault or
Manage vault from the Tools menu. If the file is missing or empty, set a
master password of 14–1024 Unicode scalars to initialize it. Unlocking applies
across all profiles and windows until TabTty exits or you change or archive
the selected vault. Copy the vault
with profile files when moving to another PC. Profiles contain credential IDs.
Use encrypted backups before changing credentials. A backup retains the master
password used when that backup was made. Open **Manage vault** to add or import
credentials, select an entry to edit or delete it, and
use the Vault file section for backups, restore, and master-password changes.

Private-key import supports the agreed OpenSSH, PKCS#8, RSA and EC envelopes.
Encrypted source-key passphrases use UTF-8 and are discarded after import.
Import leaves the source file in place. Keep the master password and backups
available separately. The application cannot recover a forgotten master password.

To start over, choose Credential vault → Archive vault and start over. TabTty
moves the existing vault to a unique sibling file. The next unlock or management
request initializes a new vault at the configured path. Select new credential
entries in any profiles that referenced the archived vault.

Before trusting a new or changed SSH host key, compare its fingerprint with one
obtained independently from the server administrator. During keyboard-interactive
authentication, the SSH server shown above the questions receives your answers.
Use the TabTty vault master password only in a local vault dialog.
Manage saved host keys separately from credentials.

New profiles start with SSH selected. Telnet uses manual login and sends passwords,
commands and output in clear text. The connected tab identifies Telnet as unencrypted.

## File transfer over SSH

Right-click the header of a connected SSH tab and choose **File transfer**.
The left pane starts in Windows Documents. The right pane starts in the
server's default folder for the SSH account shown in the window. Edit either
folder path and press Enter to navigate, or use **Parent folder** and **Refresh**.
Select files or folders and use **Upload** or **Download**, or drag the selection
between panes. Dropping onto a folder copies inside it. Dropping on a file or
blank space copies into the displayed folder.

TabTty merges matching folders and preserves items that exist only at the
destination. For a matching file, compare the names, sizes, and last-modified
times, then choose **Overwrite**, **Skip**, or **Cancel**. Cancelling keeps files
already saved. Use **Retry failed items** to start those items from the beginning
while the original connection remains available. Remote file names use the tab's
effective transmit encoding when the transfer window opens.

TabTty marks files downloaded through SFTP with the Windows Internet zone
(Mark of the Web). Choose a destination that supports named streams, such as
NTFS. If the mark cannot be saved, the download fails before replacing or
creating the final file.
Only your Windows account receives access to downloaded files, including
temporary files and replacements in shared folders. To share a completed
download, grant access through the file's Windows Security properties.
Uploads request owner-only read/write access (0600) from the server when
creating a file. The server controls the resulting access permissions.

Both panes can create folders and rename or delete files and folders. Deleting
a folder includes its contents. Remote deletion is permanent. Windows recycling
is used for local deletion where available. If the tab disconnects, check the
results in the transfer window and reopen file transfer after reconnecting.
The server must support the SFTP v3 subsystem. Remote overwrite also requires
safe replacement support.

## Recording and terminal input

Choose the recording formats and directory in the profile before connecting.
Relative directories resolve from the profile file's opening location.

- Raw stores received terminal payload bytes in `.raw.tttylog` files.
- Text stores the receive-only display transcript in `.log` files. Received LF
  finalizes a logical line. CR and BS edits are reflected, and local screen
  wrapping adds no newline. Alternate-screen output uses a simpler transcript.
- Terminal stores UTF-8 resolved display operations, including local echo,
  in `.term.tttylog` files.

Pause finishes a part. Resume creates new numbered parts without backfilling
paused output. Recording failures are visible. Retry uses a new part and never
replays uncertain writes. A slow close offers continued waiting or explicit
close with possible loss of pending log data.

Multiline paste shows a preview and separate character/line delays from 0 to
10,000 ms. Remembering delays applies across windows for that application run.
Stop Paste preserves completed input boundaries and finishes an active bracketed
paste wrapper when the connection remains usable. The initial defaults are zero
delay with remembering disabled.

Use the terminal context menu and keyboard shortcuts for copy, paste and literal
search. Search covers normal retained output. Shift allows local selection when
the remote application owns mouse reports. Named highlight sets style only regex
matches, with earlier rules taking precedence. A timed-out rule is suspended for
that tab and can be resumed explicitly.

## AI collaboration

Open a terminal tab's context menu and choose **Set up AI collaboration**.
Register the supplied executable and its stdio argument in your external MCP
host. Start TabTty before the host connects its integration. Under **Share
output with AI**, match the pairing code to the one shown by your host and
check the executable path and process ID that Windows reports for the process
that launched the bridge. Some hosts use a helper process. The host's display
name is self-reported. The launcher details identify a local process and do
not authenticate the AI product, model or individual chat.

Permissions apply to the selected bridge connection. A second connection starts
with no shared tabs, even if it reports the same name. Restarting the bridge
requires fresh sharing and a new input grant. Several chats using one bridge
connection may use its permissions.

Sharing starts in read-only mode. The AI can inspect the live terminal and the
text the tab still retains. Choose **Give control to AI** to allow terminal input.
An eye marks read-only sharing, and a terminal with a play symbol marks AI
control. The status bar shows the counterpart. **Take control from AI** stops
new AI input and keeps sharing active. If terminal input needs cleanup, wait for
**Taking control** to clear. **End AI sharing** revokes reading and input. Data
the integration already received remains with that integration.

An attached integration keeps its granted access when you lock Windows, switch
users, or disconnect Remote Desktop. To stop access before stepping away,
choose **End AI sharing** in the tab's status bar.

Choose **End AI sharing** before entering a password or one-time code in the
terminal. Whenever you share a tab, the integration can read its current screen
and retained history, including output from before sharing and from intervals
when sharing was off. This includes text echoed locally or by the remote system
and text hidden by terminal conceal styling. If sensitive text reached the
screen or history, keep that tab unshared and use a fresh terminal session for AI
collaboration.

Shared tabs cannot be broadcast targets. An active or paused macro keeps its own
input authority while the AI can inspect its shared output.

For closed-network operation, provision the AI host, its inference runtime, and
the model before use. Configure the host to use local inference or a server
allowed by that network. The TabTty package contains its stdio bridge and
runtime.

## Application files

Unpackaged application data resides under `%LOCALAPPDATA%\TabTty`. Settings,
highlighting, registrations/window state and SSH host keys have independent JSON
stores. Settings includes explicit backup restore and recreation. Recovery keeps
the previous file under a unique recovery name. Temporary defaults are read-only
until deliberate recovery. Removing a registration preserves the profile file.
Deleting a profile file requests the Windows Recycle Bin.

TabTty sends no telemetry and provides no automatic updater.
Updates replace the extracted application folder while
profile, vault and application data remain in their chosen locations.
