# Received backspace after local echo in Tera Term and RLogin

**Both terminals apply a received BS to the visible cursor.** Suppose the peer
sends `AB`, the user types `x` with local echo enabled, and the peer then sends
`BS Z CR LF` without echoing `x`. The cursor was in column four after `ABx`.
BS moves it to column three, leaving `x` visible. The arriving `Z` overwrites
`x`, leaving **`ABZ`** on the first row. A cursor tracking received data alone
would have been in column three before BS and would produce **`AZ`**.
[Tera Term BS handler](https://github.com/TeraTermProject/teraterm/blob/ae000ef3ab4eeda0d078a430465fbf4347c15e45/teraterm/teraterm/vtterm.c#L661-L673),
[RLogin BS handler](https://github.com/kmiya-culti/RLogin/blob/85f9ec25f6f408157bc45a6b53ab7a6a2f4b4097/RLogin/TextRamProc.cpp#L2897-L2924).

This 2026-11-26 review derives the example from the released source of
**Tera Term 5.7.1** and **RLogin for Windows 2.32.0**. It assumes one empty
normal-screen row, ordinary single-column ASCII in overwrite mode, room to
the right, and logging active throughout. The results have not been tested
in running clients. [Tera Term release](https://github.com/TeraTermProject/teraterm/releases/tag/v5.7.1),
[RLogin release](https://github.com/kmiya-culti/RLogin/releases/tag/2.32.0).

## What the logs contain

Text entries below show the first row's content before its line ending, with
timestamps off. `U+0008` denotes an actual Backspace control character.

| Client and setting | Result for the example | How the client records it |
| --- | --- | --- |
| Tera Term Text, Plain text enabled | `ABxZ` | Printable characters are logged as processed. BS moves the cursor and is omitted from the file. [Printable output](https://github.com/TeraTermProject/teraterm/blob/ae000ef3ab4eeda0d078a430465fbf4347c15e45/teraterm/teraterm/vtterm.c#L941-L953), [BS handling](https://github.com/TeraTermProject/teraterm/blob/ae000ef3ab4eeda0d078a430465fbf4347c15e45/teraterm/teraterm/vtterm.c#L661-L673). |
| Tera Term Text, Plain text disabled | `ABx` + U+0008 + `Z` | The same cursor movement emits the BS control into the text log. The log encodes the line ending according to its settings. [BS handling](https://github.com/TeraTermProject/teraterm/blob/ae000ef3ab4eeda0d078a430465fbf4347c15e45/teraterm/teraterm/vtterm.c#L661-L673), [text log writer](https://github.com/TeraTermProject/teraterm/blob/ae000ef3ab4eeda0d078a430465fbf4347c15e45/teraterm/teraterm/vtterm.c#L422-L470). |
| Tera Term Binary | `41 42 78 08 5A 0D 0A` | Local echo inserts the encoded `x` into the input buffer. The binary logger sees that byte (`78`) when the buffer is read. [Local echo](https://github.com/TeraTermProject/teraterm/blob/ae000ef3ab4eeda0d078a430465fbf4347c15e45/teraterm/ttpcmn/ttcmn_buff.c#L450-L467), [binary logging](https://github.com/TeraTermProject/teraterm/blob/ae000ef3ab4eeda0d078a430465fbf4347c15e45/teraterm/ttpcmn/ttcmn_buff.c#L84-L134). |
| RLogin RAW | `41 42 08 5A 0D 0A` | The socket receive handler writes the received bytes. Local echo goes directly to the terminal model. [Socket receive](https://github.com/kmiya-culti/RLogin/blob/85f9ec25f6f408157bc45a6b53ab7a6a2f4b4097/RLogin/RLoginDoc.cpp#L2196-L2217), [local echo](https://github.com/kmiya-culti/RLogin/blob/85f9ec25f6f408157bc45a6b53ab7a6a2f4b4097/RLogin/RLoginView.cpp#L1154-L1160). |
| RLogin CTRL or CHAR | `ABx<BS>Z` | Both modes write BS as the four printable characters `<BS>`. Ordinary locally echoed characters pass through the same character callback. [Local byte dispatch](https://github.com/kmiya-culti/RLogin/blob/85f9ec25f6f408157bc45a6b53ab7a6a2f4b4097/RLogin/TextRam.cpp#L11669-L11683), [printable callback](https://github.com/kmiya-culti/RLogin/blob/85f9ec25f6f408157bc45a6b53ab7a6a2f4b4097/RLogin/TextRam.cpp#L10668-L10676), [log formatting](https://github.com/kmiya-culti/RLogin/blob/85f9ec25f6f408157bc45a6b53ab7a6a2f4b4097/RLogin/TextRam.cpp#L8565-L8606). |
| RLogin LINE | `ABZ` | It reads the edited display cells. The prior row is saved when a character callback observes a different row, or the pending row is saved at log close. [Line selection and writing](https://github.com/kmiya-culti/RLogin/blob/85f9ec25f6f408157bc45a6b53ab7a6a2f4b4097/RLogin/TextRam.cpp#L8497-L8514), [row change](https://github.com/kmiya-culti/RLogin/blob/85f9ec25f6f408157bc45a6b53ab7a6a2f4b4097/RLogin/TextRam.cpp#L8553-L8565), [log close](https://github.com/kmiya-culti/RLogin/blob/85f9ec25f6f408157bc45a6b53ab7a6a2f4b4097/RLogin/RLoginDoc.cpp#L1667-L1682). |
| RLogin PAGE | `ABZ` if that row is saved in this state | PAGE records rows as they scroll into history and saves remaining rows at log close. Subsequent screen edits or clears before capture can change what survives. [History capture](https://github.com/kmiya-culti/RLogin/blob/85f9ec25f6f408157bc45a6b53ab7a6a2f4b4097/RLogin/TextRam.cpp#L10419-L10428), [remaining rows](https://github.com/kmiya-culti/RLogin/blob/85f9ec25f6f408157bc45a6b53ab7a6a2f4b4097/RLogin/TextRam.cpp#L2796-L2818), [RLogin logging FAQ](https://kmiya-culti.github.io/RLogin/faq.html#LOGFILE). |

## Reading the example against TabTty's specification

TabTty also specifies **`ABZ` on screen**. Its normal-screen **Text log records
`AZ`** because its cursor receives only `AB` before BS moves it back onto
`B`. The **Raw log** holds `41 42 08 5A 0D 0A`. The **Terminal log** includes
the locally echoed `x` and the subsequent BS as ordered display operations,
so this ASCII example has bytes `41 42 78 08 5A 0D 0A`. RLogin LINE and
TabTty Text both record edited text, but the former reads the visible row and
the latter reads the receive-only row. [TabTty receive and display contract](../specification/session-and-recording.md#receive-and-display-operations),
[TabTty Terminal recording](../drafts/receive-logging-issue.md#live-display-boundary-independent-of-log-format).
