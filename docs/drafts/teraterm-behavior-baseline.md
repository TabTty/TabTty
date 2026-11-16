# Tera Term behavior reference for TabTty

> The 2026-11-10 baseline was consolidated in [implementation readiness and completed contracts](../specification/implementation-readiness.md). See [implementation status](../specification/implementation-status.md) for validation evidence.

Reference: Tera Term 5.7.1 (`v5.7.1`, release commit `ae000ef`), released
2026-10-09 and reviewed 2026-11-08. Use version-pinned settings, documentation,
and source when resolving differences from the rolling online manual.

## Decision authority and boundaries

The user accepted Q100, accepted Q101 with ARM64 optional, and accepted Q102.
For Q103 onward, the user requested decisions based on Tera Term. The decisions below are made
under that delegation, rather than leaving routine compatibility choices as
repeated approval questions. The selected application foundation is WPF on
.NET 10 LTS with self-contained deployment.

Use Tera Term as a behavior and interoperability reference. This does not adopt
its source, binaries, libraries, UI framework, configuration format, or complete
feature set. Preserve the existing Microsoft/OpenSSL dependency boundary and
the accepted TabTty SSH architecture. Referencing TTSSH does not authorize
adopting LibreSSL or a complete third-party SSH client.

Previously agreed TabTty behavior takes precedence, including saved `.ttty`
profiles, UTF-8 default and manual encoding selection, the three logging modes,
newline semantics, IME/local echo, paste confirmation and in-memory delays,
fixed default terminal colors, multi-window tabs, and the portable vault.
The separately agreed [file transfer contract](../specification/file-transfer.md)
adds SFTP to the initial release. The
[file transfer decisions](file-transfer-issue.md#filename-encoding-references-for-q25)
record the Tera Term and RLogin comparison and the choice for remote names.

Security-sensitive decisions must also follow `authentication-issue.md` and
current guidance. A reference behavior that conflicts with an accepted
requirement must be identified and adapted, not silently copied. Reference
coverage does not by itself prove TabTty compatibility or implementation
readiness.

## Q103: Selected terminal baseline

Tera Term ships `TerminalID=VT100` and `TermType=xterm`. Its terminal ID,
advertised SSH/Telnet name, and implemented extensions are separate concepts.
Select `xterm` as TabTty's default advertised type, editable per profile;
serial does not negotiate a TERM value. Select VT100 primary device-attributes
identification (`ESC [ ? 1 ; 2 c`) for the initial baseline. Do not imply that
every VT100-through-VT525 personality is implemented or selectable.

Support the cursor movement, erase/insert/delete, scrolling regions, DEC line
graphics, character attributes, cursor save/restore, normal/alternate screens,
application cursor/keypad modes, and status/cursor-position responses needed
for the already agreed ordinary `less`, `top`, and `vim` use. Reset each new
session to autowrap on, application cursor/keypad modes off, and mouse reporting
off; enable supported modes only through their corresponding controls.

Use 16- and 256-color addressing. Retain actual 24-bit RGB values in TabTty
when supported SGR RGB selections are received, rather than reducing them to a
256-color palette as the reference does. This is an explicit TabTty extension,
not a claim of identical color rendering. Fixed white/black defaults and the
agreed highlight/search/selection precedence remain unchanged.

For mouse interoperability, select X10 and modes 1000, 1002, and 1003, with
classic reports and SGR cell-coordinate reports (1006). Defer UTF-8 mouse
coordinates (1005), urxvt coordinates (1015), SGR pixel coordinates (1016), and
DEC locator extensions. Preserve the agreed Shift override and gesture-owner
rules; protocol limits and mode-change/release details remain acceptance work.

For Unicode ambiguous-width characters, use one cell by default with an explicit
per-profile choice of one or two cells. Do not change this with UI language.
Resolve the setting when connecting. This is TabTty's language-independent
selection, not a universal Tera Term installed default. A 2026-11-08 recheck of
the pinned installer corrected the earlier description: Japanese/Chinese/Korean
Windows UI selects ambiguous width two and an enabled two-cell emoji override;
other language branches use ambiguous width one and no emoji override. The
accepted TabTty default remains one, independent of UI language. Legacy
encoding-specific width, grapheme/emoji cases, Unicode data version, and font
fallback require detailed acceptance cases. Q117-Q120 in
`terminal-display-proposals.md` accept the font/zoom, Unicode width direction,
fixed cursor behavior (without user choices), and initial accessibility scope;
the remaining detailed compatibility matrix is not yet complete.

The same installer uses environment-dependent initial fonts (Japanese MS Gothic
versus Terminal in the default branch); it does not establish Consolas 12 pt.
The distributed INI selects a blinking block cursor and an unfocused outline.
Those verified facts informed Q117/Q119 without implying general font/rendering
equivalence between Tera Term and TabTty.

Sources: [pinned installer defaults](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/installer/teraterm.iss#L440),
[pinned cursor configuration](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/installer/release/TERATERM.INI#L168),
and [pinned width/combining logic](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/buffer.c#L2454).

The reference's buffer-resize code copies the smaller old/new row width, which
can discard right-hand cells instead of reflowing text. Q106 now selects normal
reflow, traditional alternate-screen resize, preserved content anchors, and
eligible Text flushing only on actual quota retirement. See
`resize-and-vault-proposals.md`. The no-synthetic-newline rule remains in force.

## Q104: Selected serial baseline

| Profile field | Default | Initial choices |
| --- | --- | --- |
| Baud rate | 9600 | Common presets plus positive-integer entry, subject to driver support |
| Data bits | 8 | 7 or 8 |
| Parity | None | None, Odd, Even, Mark, Space |
| Stop bits | 1 | 1 or 2 |
| Flow control | None | None, XON/XOFF, RTS/CTS, DSR/DTR |

These defaults and choices follow the reference, replacing the unaccepted
115200/5-8-data-bit/1.5-stop-bit/combined-flow proposal. Do not expose an API's
enum as the product's compatibility policy. In particular, Microsoft's
SerialPort Handshake enum lacks DSR/DTR, while official Win32 DCB supports it;
the adapter design must preserve this selected feature set within the existing
Microsoft dependency policy.

Save the explicitly chosen COM name as the destination. Show detected ports
with friendly names where available, offer refresh, and allow specifying and
retaining a currently absent COM name. A friendly name is descriptive, not a
stable device identity. Do not substitute another COM port or follow a USB
device to a different assignment automatically. Keep the previously agreed
manual-reconnect/no-replay policy even though the reference enables automatic
serial reconnection by default. Do not impose its default 256-item list limit
on Windows enumeration without an independent reason.

Validate requested values and report missing/busy ports and rejected settings.
Check the driver/API result rather than silently substituting settings or
reporting successful connection after configuration failure. The requirement
is a Windows-recognized usable COM device, not arbitrary driver-free hardware.

### Signal controls and BREAK

- Profile DTR and RTS default to On, following the reference's Enable defaults.
  Offer On/Off for a line not managed by selected flow control.
- RTS/CTS owns RTS in handshake mode; DSR/DTR owns DTR in handshake mode.
  Disable manual control of an owned line. The other line remains configurable.
- Provide explicit temporary DTR On/Off and RTS On/Off session operations for
  lines outside handshake control. Do not save those operations to the profile;
  reconnect restores the saved values. Saved transport-setting edits still
  apply on the next connection.
- Provide an explicit Send break operation, with a fixed initial duration of
  1000 ms, matching the reference default. It is a serial signal operation,
  not text or a synthetic received character.
- Limit the initial UI to these controls. Independently configuring handshake
  outside the selected flow policy, RTS Toggle, and adjustable BREAK duration
  are not initial requirements. This is a scope adaptation from the reference.
- Preserve existing paste-only delay settings. Do not adopt the reference's
  persistent per-serial-profile transmit delays or a macro dependency.

## Q105: Selected Telnet baseline

Use an explicit Telnet profile with hostname/IPv4/IPv6 and a separate TCP port,
defaulting to 23. Start normal Telnet negotiation on every configured port;
do not infer protocol behavior from whether the port equals 23. This adapts
the reference, which normally initiates negotiation only when its TCP port
equals its configured Telnet port. Raw TCP auto-detection and a passive-only
startup option are outside the initial scope.

Support BINARY, ECHO, SGA, TERMINAL-TYPE, and NAWS. Initially offer TTYPE and
NAWS, request remote ECHO, and offer/request SGA. Do not accept a request that
TabTty echo received data back onto the connection. Unknown or unsupported
options are refused correctly. TSPEED and the reference's other optional
extensions are outside this initial set. This is not a full RFC-conformance or
Tera Term feature-parity claim; basic command handling still needs review.

Expose `Initiate BINARY negotiation` in the profile, default Off, following
`TelBin=off`. Off means not requesting BINARY proactively; it does not mean
refusing the peer's request. Accept peer BINARY requests and track send/receive
states independently. When On, request BINARY in both directions at startup.
Use explicit directional negotiation states, including pending/queued changes,
with RFC 1143 as the design reference to avoid negotiation loops.

Whether or not BINARY has been agreed, preserve all terminal data bits and the
selected character encoding. Do not mask to seven bits, switch to ASCII, or
refuse Japanese solely because BINARY was not negotiated. This follows the
reference's practical eight-bit NVT behavior; it does not make a seven-bit-only
peer accept Japanese. Non-BINARY CR/NUL handling remains active until BINARY
is agreed in the applicable direction. IAC escaping/parsing applies in both.

Keep the previously agreed Telnet Enter default CRLF and configurable CR/LF/CRLF
mapping. Non-BINARY standalone CR is encoded as CR NUL, CRLF as CR LF, and LF as
LF. BINARY sends those mapped bytes without NVT CR conversion. Escape data IAC
on every outgoing data path, including encoded text; do not limit escaping to
one send API. Preserve receiver state across split IAC, CR, and subnegotiation.

For TTYPE, return the configured terminal-name string after negotiation and a
SEND request; do not cycle terminal candidates or introduce MTTS. Send NAWS
with current terminal columns/rows after agreement and on size changes, using
16-bit network-order values and escaped IAC. Advertise viewport dimensions,
not the retained scrollback limit. Do not accept peer NAWS as an instruction to
resize TabTty's window in the initial release.

Keep immediate submission of input with the already agreed ordering/pacing,
and do not introduce the reference's initial local line-buffering mode.
Keep TabTty's accepted Follow negotiation/Off local-echo policy rather than
copying `TelEcho=off`. These are intentional adaptations to prior decisions.

Provide explicit Are you there (AYT) and Send break Telnet commands, following
the reference Control menu. Telnet BREAK is a protocol command, independent of
the serial BREAK duration. Do not translate Ctrl+C to Telnet IP automatically.
Dedicated IP/AO sending controls and full SYNCH/urgent-data behavior require
separate protocol-scope review rather than an unsupported compliance claim.

### Telnet Raw recording boundary

The agreed Raw mode records exact received terminal payload bytes after Telnet
transport decoding, before character decoding. The reference also logs at this
boundary. This does not mean an exact TCP-stream capture:

| Received wire representation | Terminal payload supplied to Raw |
| --- | --- |
| Non-BINARY `0D 00` | `0D` |
| `0D 0A` | `0D 0A` |
| Escaped data `FF FF` | `FF` |
| BINARY `0D 00` | `0D 00` |
| Telnet negotiation/commands | No payload bytes |

No local echo, sent protocol commands, authentication-protocol traffic, or
local diagnostics are added to Raw. Preserve existing Text/Terminal eligibility
and control handling after this transport boundary; this clarification does
not expand recording scope or introduce newline normalization in Raw.

## Source index

- [Pinned release](https://github.com/TeraTermProject/teraterm/releases/tag/v5.7.1)
- [Shipped default configuration](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/installer/release/TERATERM.INI)
- [Terminal settings](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/doc/en/html/menu/setup-additional-terminal.html)
- [Serial settings](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/doc/en/html/menu/setup-additional-serialport.html)
- [VT responses, modes, and RGB handling](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/vtterm.c)
- [Buffer resizing and Unicode width](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/buffer.c)
- [Terminal control reference](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/doc/en/html/about/ctrlseq.html)
- [Serial option lists and signal configuration](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/serial_pp.cpp)
- [Serial configuration and port opening](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/commlib.c)
- [BREAK and initial Telnet negotiation](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/vtwin.cpp)
- [Telnet options, TTYPE, NAWS, AYT, and BREAK](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/telnet.c)
- [Telnet receive payload and logging boundary](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/ttpcmn/ttcmn_buff.c)
- [Microsoft serial handshake choices](https://learn.microsoft.com/en-us/dotnet/api/system.io.ports.handshake)
- [Microsoft serial DCB controls](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-dcb)
- [RFC 1143 option negotiation](https://www.rfc-editor.org/rfc/rfc1143.html)

## Remaining reference work

The basic compatibility decisions in this review do not close every remaining
branch in `specification-frontier.md`. In particular, complete the capability
matrix, encoding mapping, detailed event ordering, bounded input/recording
pipelines, profile/credential lifecycle, vault format, and release acceptance
criteria before reporting readiness. Where Tera Term has no equivalent feature,
design the TabTty-specific behavior explicitly instead of inventing parity.
