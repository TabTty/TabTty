# Connection profile trust validation

Validation recorded on 2026-11-19 for [TabTty #68](https://github.com/TabTty/TabTty/issues/68)
and its eight implementation tasks. See [Connection profile registration and trust](profile-trust.md).
The 2026-11-23 import and reconnect decisions postdate this evidence. See
the [current acceptance cases](profile-trust.md#acceptance-cases).

## Passed

| Check | Coverage |
| --- | --- |
| Windows Release solution build | App, Core, MCP bridge, and test assemblies |
| App profile-trust acceptance | Source-read cancellation, manual and macro choices, registration of the reviewed bytes, re-review after an external edit, main-window and standalone editor saves, registration removal, reset across registered and unregistered locations, a stale reply after reset, and Japanese actions |
| Windows recording destination acceptance | Real Windows drive reassignment using a temporary test drive, recording retention at the reviewed location, rejection of a changed location at open and resume, and retry after restoring the mapping |
| Core and App suites | Saved profiles, terminal connections and input, recording, SSH and vault interactions, macro launch and allow/block reset, and window behavior |
| Desktop integration | Editor file publication and recovery, terminal close, broadcast and splits, and AI collaboration |
| Loopback SSH fixture | SFTP batch copying and transfer-window close |
| Packaged MCP smoke test | Separate attachments, discovery, GUI activation, and failure reporting when the GUI is absent |

## Not run

| Check | Missing environment |
| --- | --- |
| Open and exchange data through a real COM port after profile approval | The Windows host exposed zero COM ports. The Core suite exercised serial settings and the missing-port outcome. |
| Approve a profile read from a live SMB test share and exercise logging across two SMB drive targets | The non-administrator test session had no dedicated test share or mapped SMB drive. The suite exercised source-read cancellation and a real local drive reassignment. |

The Windows API contracts were checked against the Microsoft documentation for
[WNetGetConnectionW](https://learn.microsoft.com/en-us/windows/win32/api/winnetwk/nf-winnetwk-wnetgetconnectionw)
and [CredDeleteW](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-creddeletew).
