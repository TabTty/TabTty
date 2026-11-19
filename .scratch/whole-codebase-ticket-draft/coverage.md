# Accepted verification coverage

This is an assignment of future verification, not executed test evidence. Each ticket owns its delivered path; a row may have multiple owners for distinct transports or formats. The final cross-owner ticket adds composition cases without replacing these checks.

## Candidate 1

| Case | Accepted scenario | Owning draft tickets |
| --- | --- | --- |
| 1 | Healthy Pause with selected Raw/Text/Terminal formats | [01](issues/01-own-recording-pause-resume-and-retry-through-complete-operations.md) |
| 2 | Resume/retry or initial preparation succeeds, fails partway, or is invalidated before activation | [01](issues/01-own-recording-pause-resume-and-retry-through-complete-operations.md) |
| 3 | Receive EOF, then later disconnect/close | [02](issues/02-finalize-recordings-once-across-receive-eof-and-disconnect.md) |
| 4 | Recording disabled or repeated completed operations | [01](issues/01-own-recording-pause-resume-and-retry-through-complete-operations.md), [02](issues/02-finalize-recordings-once-across-receive-eof-and-disconnect.md) |
| 5 | Active recording has pending I/O with no completed progress for ten seconds | [01](issues/01-own-recording-pause-resume-and-retry-through-complete-operations.md) |
| 6 | Explicit close is accepted while a write/flush or Pause drain is pending, including near the watchdog deadline | [03](issues/03-preserve-recording-work-throughout-close-waiting-and-later-loss-decisions.md) |
| 7 | Continue waiting, then delayed successful I/O completion | [03](issues/03-preserve-recording-work-throughout-close-waiting-and-later-loss-decisions.md) |
| 8 | Continue waiting, then the later explicit close-with-loss action | [03](issues/03-preserve-recording-work-throughout-close-waiting-and-later-loss-decisions.md) |
| 9 | Cancellation is delayed or an issued synchronous flush remains pending | [03](issues/03-preserve-recording-work-throughout-close-waiting-and-later-loss-decisions.md) |
| 10 | Actual write/flush/close failure, including a partly written suffix | [01](issues/01-own-recording-pause-resume-and-retry-through-complete-operations.md), [03](issues/03-preserve-recording-work-throughout-close-waiting-and-later-loss-decisions.md) |
| 11 | Repeated tab close or a window close joins existing work | [02](issues/02-finalize-recordings-once-across-receive-eof-and-disconnect.md), [03](issues/03-preserve-recording-work-throughout-close-waiting-and-later-loss-decisions.md) |
| 12 | EOF/disconnect notification or final closure reenters the owner | [02](issues/02-finalize-recordings-once-across-receive-eof-and-disconnect.md), [03](issues/03-preserve-recording-work-throughout-close-waiting-and-later-loss-decisions.md) |
| 13 | Log work is complete while transport remains pending, or saving completes before a delayed loss answer | [03](issues/03-preserve-recording-work-throughout-close-waiting-and-later-loss-decisions.md) |
| 14 | Caller cancels observation, a prompt fails to display, or a view disappears | [01](issues/01-own-recording-pause-resume-and-retry-through-complete-operations.md), [03](issues/03-preserve-recording-work-throughout-close-waiting-and-later-loss-decisions.md) |
| 15 | Old-part completion/failure or a delayed open arrives during retry or final close | [01](issues/01-own-recording-pause-resume-and-retry-through-complete-operations.md), [02](issues/02-finalize-recordings-once-across-receive-eof-and-disconnect.md), [03](issues/03-preserve-recording-work-throughout-close-waiting-and-later-loss-decisions.md) |
| 16 | Cleanup allowance is unavailable or occupied by failed prior work | [01](issues/01-own-recording-pause-resume-and-retry-through-complete-operations.md), [03](issues/03-preserve-recording-work-throughout-close-waiting-and-later-loss-decisions.md) |

## Candidate 2

| Case | Accepted scenario | Owning draft tickets |
| --- | --- | --- |
| 1 | Initial log preparation fails; otherwise start SSH/Telnet/serial | [04](issues/04-run-telnet-sessions-through-complete-terminal-payload-transport-operations.md), [05](issues/05-run-serial-sessions-through-the-terminal-payload-transport-seam.md), [18](issues/18-deliver-ssh-terminal-startup-and-interaction-through-the-shared-transport-and-channel-owners.md) |
| 2 | SSH DATA/stderr before PTY/shell success, then success, refusal, timeout or cancellation | [18](issues/18-deliver-ssh-terminal-startup-and-interaction-through-the-shared-transport-and-channel-owners.md) |
| 3 | Many receive fragments with DATA/stderr, Telnet IAC/NVT CR state and negotiated echo changes | [04](issues/04-run-telnet-sessions-through-complete-terminal-payload-transport-operations.md), [18](issues/18-deliver-ssh-terminal-startup-and-interaction-through-the-shared-transport-and-channel-owners.md) |
| 4 | A payload generates a terminal reply while peer send credit is zero, including rekey/window adjustment | [18](issues/18-deliver-ssh-terminal-startup-and-interaction-through-the-shared-transport-and-channel-owners.md) |
| 5 | Consumer is held while legal advertised payload arrives, including over 1024 tiny alternating DATA/stderr messages | [18](issues/18-deliver-ssh-terminal-startup-and-interaction-through-the-shared-transport-and-channel-owners.md) |
| 6 | Ordered echo/control metadata grows without corresponding payload credit, or pressure cannot recover | [04](issues/04-run-telnet-sessions-through-complete-terminal-payload-transport-operations.md), [18](issues/18-deliver-ssh-terminal-startup-and-interaction-through-the-shared-transport-and-channel-owners.md) |
| 7 | Disk write/flush stalls or fails while receive processing is healthy | [04](issues/04-run-telnet-sessions-through-complete-terminal-payload-transport-operations.md), [05](issues/05-run-serial-sessions-through-the-terminal-payload-transport-seam.md), [18](issues/18-deliver-ssh-terminal-startup-and-interaction-through-the-shared-transport-and-channel-owners.md) |
| 8 | Geometry changes during authentication, PTY/shell startup and ordinary operation | [04](issues/04-run-telnet-sessions-through-complete-terminal-payload-transport-operations.md), [18](issues/18-deliver-ssh-terminal-startup-and-interaction-through-the-shared-transport-and-channel-owners.md) |
| 9 | Partial send, Stop Paste, cancelled observation, and Disconnect race | [04](issues/04-run-telnet-sessions-through-complete-terminal-payload-transport-operations.md), [05](issues/05-run-serial-sessions-through-the-terminal-payload-transport-seam.md), [18](issues/18-deliver-ssh-terminal-startup-and-interaction-through-the-shared-transport-and-channel-owners.md) |
| 10 | EOF followed by permitted sending, repeated EOF/close, or close without EOF | [04](issues/04-run-telnet-sessions-through-complete-terminal-payload-transport-operations.md), [05](issues/05-run-serial-sessions-through-the-terminal-payload-transport-seam.md), [18](issues/18-deliver-ssh-terminal-startup-and-interaction-through-the-shared-transport-and-channel-owners.md) |
| 11 | Consumer throws after partial application or cannot accept lifecycle facts | [04](issues/04-run-telnet-sessions-through-complete-terminal-payload-transport-operations.md), [05](issues/05-run-serial-sessions-through-the-terminal-payload-transport-seam.md), [18](issues/18-deliver-ssh-terminal-startup-and-interaction-through-the-shared-transport-and-channel-owners.md) |
| 12 | Delayed reads/writes/open/close complete after cancellation, release observation or replacement | [04](issues/04-run-telnet-sessions-through-complete-terminal-payload-transport-operations.md), [05](issues/05-run-serial-sessions-through-the-terminal-payload-transport-seam.md), [18](issues/18-deliver-ssh-terminal-startup-and-interaction-through-the-shared-transport-and-channel-owners.md) |
| 13 | Repeated Start/Disconnect and concurrent closure/recording actions | [04](issues/04-run-telnet-sessions-through-complete-terminal-payload-transport-operations.md), [05](issues/05-run-serial-sessions-through-the-terminal-payload-transport-seam.md), [18](issues/18-deliver-ssh-terminal-startup-and-interaction-through-the-shared-transport-and-channel-owners.md) |
| 14 | SFTP activity, serial line/BREAK actions, and real peers/devices | [04](issues/04-run-telnet-sessions-through-complete-terminal-payload-transport-operations.md), [05](issues/05-run-serial-sessions-through-the-terminal-payload-transport-seam.md) |

## Candidate 3

| Case | Accepted scenario | Owning draft tickets |
| --- | --- | --- |
| 1 | Ordinary update, create and authorized restore | [06](issues/06-save-connection-profiles-through-owned-verified-file-operations.md), [07](issues/07-save-and-recover-application-stores-through-verified-operations.md), [08](issues/08-publish-encrypted-vault-revisions-with-verified-preservation.md) |
| 2 | Replacement reports error after candidate becomes target | [06](issues/06-save-connection-profiles-through-owned-verified-file-operations.md), [07](issues/07-save-and-recover-application-stores-through-verified-operations.md), [08](issues/08-publish-encrypted-vault-revisions-with-verified-preservation.md) |
| 3 | Candidate installed but preservation missing, different or unreadable | [06](issues/06-save-connection-profiles-through-owned-verified-file-operations.md), [07](issues/07-save-and-recover-application-stores-through-verified-operations.md), [08](issues/08-publish-encrypted-vault-revisions-with-verified-preservation.md) |
| 4 | Original intact, create target absent, unexpected target, target missing/damaged or unreadable | [06](issues/06-save-connection-profiles-through-owned-verified-file-operations.md), [07](issues/07-save-and-recover-application-stores-through-verified-operations.md), [08](issues/08-publish-encrypted-vault-revisions-with-verified-preservation.md) |
| 5 | New-file collision and external change between comparison/publication/verification | [06](issues/06-save-connection-profiles-through-owned-verified-file-operations.md), [07](issues/07-save-and-recover-application-stores-through-verified-operations.md), [08](issues/08-publish-encrypted-vault-revisions-with-verified-preservation.md) |
| 6 | Stale editor basis, case-sensitive names, short/long aliases and known linked locations | [06](issues/06-save-connection-profiles-through-owned-verified-file-operations.md), [07](issues/07-save-and-recover-application-stores-through-verified-operations.md), [08](issues/08-publish-encrypted-vault-revisions-with-verified-preservation.md) |
| 7 | Replacement changes file identity; new request arrives through the same name/known alias | [06](issues/06-save-connection-profiles-through-owned-verified-file-operations.md), [07](issues/07-save-and-recover-application-stores-through-verified-operations.md), [08](issues/08-publish-encrypted-vault-revisions-with-verified-preservation.md) |
| 8 | Candidate buffer/draft changes after validation, or same-ID vault has different bytes | [06](issues/06-save-connection-profiles-through-owned-verified-file-operations.md), [08](issues/08-publish-encrypted-vault-revisions-with-verified-preservation.md) |
| 9 | Separately chosen restore image or corrupt original | [07](issues/07-save-and-recover-application-stores-through-verified-operations.md), [08](issues/08-publish-encrypted-vault-revisions-with-verified-preservation.md) |
| 10 | Cancellation before/after publication issue, lost wait and concurrent reconciliation | [06](issues/06-save-connection-profiles-through-owned-verified-file-operations.md), [07](issues/07-save-and-recover-application-stores-through-verified-operations.md), [08](issues/08-publish-encrypted-vault-revisions-with-verified-preservation.md) |
| 11 | Cleanup error or reused candidate pathname | [06](issues/06-save-connection-profiles-through-owned-verified-file-operations.md), [07](issues/07-save-and-recover-application-stores-through-verified-operations.md), [08](issues/08-publish-encrypted-vault-revisions-with-verified-preservation.md) |
| 12 | Password-change uncertainty and unrelated external vault stale | [08](issues/08-publish-encrypted-vault-revisions-with-verified-preservation.md) |
| 13 | View closure, late verification/I/O and quiescent exit | [06](issues/06-save-connection-profiles-through-owned-verified-file-operations.md), [07](issues/07-save-and-recover-application-stores-through-verified-operations.md), [08](issues/08-publish-encrypted-vault-revisions-with-verified-preservation.md) |
| 14 | Operation/byte/artifact capacity reached, repeated cancellations or retired IDs | [06](issues/06-save-connection-profiles-through-owned-verified-file-operations.md), [07](issues/07-save-and-recover-application-stores-through-verified-operations.md), [08](issues/08-publish-encrypted-vault-revisions-with-verified-preservation.md) |
| 15 | Profile publication succeeds but registration/settings update fails | [06](issues/06-save-connection-profiles-through-owned-verified-file-operations.md), [07](issues/07-save-and-recover-application-stores-through-verified-operations.md) |

## Candidate 4

| Case | Accepted scenario | Owning draft tickets |
| --- | --- | --- |
| 1 | Independent unlock/restore fixtures; invalid framing, tag, schema, key material or failed RNG | [09](issues/09-own-shared-vault-unlock-and-credential-handoff-operations.md), [11](issues/11-change-the-vault-master-password-through-one-owned-operation.md), [12](issues/12-back-up-and-restore-vaults-through-complete-authenticated-operations.md) |
| 2 | Entry add/edit/delete through management tab and same-file editor | [10](issues/10-manage-vault-entries-and-key-imports-through-owned-operations.md) |
| 3 | Same-ID copies, known aliases and candidate 3 publication competing with archive/initialization | [13](issues/13-create-vaults-only-through-explicit-guarded-initialization.md), [14](issues/14-archive-the-confirmed-vault-image-without-requiring-unlock.md) |
| 4 | Shared unlock with observation cancellation versus one consumer's stop request | [09](issues/09-own-shared-vault-unlock-and-credential-handoff-operations.md) |
| 5 | Last unlock consumer leaves; native success and a new request arrive later | [09](issues/09-own-shared-vault-unlock-and-credential-handoff-operations.md) |
| 6 | Host closes; old/new host replies, duplicate replies or adapter failure race | [09](issues/09-own-shared-vault-unlock-and-credential-handoff-operations.md), [15](issues/15-coordinate-vault-selection-and-closing-with-owned-operations.md) |
| 7 | Entry revision, confirmed file image or known impact facts change during review | [09](issues/09-own-shared-vault-unlock-and-credential-handoff-operations.md), [10](issues/10-manage-vault-entries-and-key-imports-through-owned-operations.md), [11](issues/11-change-the-vault-master-password-through-one-owned-operation.md), [12](issues/12-back-up-and-restore-vaults-through-complete-authenticated-operations.md), [14](issues/14-archive-the-confirmed-vault-image-without-requiring-unlock.md) |
| 8 | Selected context across A-to-B-to-A; editor closes while shared owner remains | [09](issues/09-own-shared-vault-unlock-and-credential-handoff-operations.md), [15](issues/15-coordinate-vault-selection-and-closing-with-owned-operations.md) |
| 9 | Unlock/authentication/all pickers/Explorer versus explicit management initialization | [09](issues/09-own-shared-vault-unlock-and-credential-handoff-operations.md), [13](issues/13-create-vaults-only-through-explicit-guarded-initialization.md) |
| 10 | Empty original parked; install fails, conflicts or becomes uncertain; compensation fails | [13](issues/13-create-vaults-only-through-explicit-guarded-initialization.md) |
| 11 | Import with source change, unsupported key, missing/wrong passphrase or cancellation during native work | [10](issues/10-manage-vault-entries-and-key-imports-through-owned-operations.md) |
| 12 | Master change with candidate at target but preservation missing/unreadable | [11](issues/11-change-the-vault-master-password-through-one-owned-operation.md) |
| 13 | Ordinary encrypted backup and external-stale rescue copy | [12](issues/12-back-up-and-restore-vaults-through-complete-authenticated-operations.md) |
| 14 | Locked/unlocked restore with known-ID mismatch, unknown damaged ID and recognizable newer-version/flags fence | [12](issues/12-back-up-and-restore-vaults-through-complete-authenticated-operations.md) |
| 15 | Archive of locked/readable damaged bytes, or successful archive after an exception | [14](issues/14-archive-the-confirmed-vault-image-without-requiring-unlock.md) |
| 16 | Archive destination verified but source reappears, differs or cannot be inspected | [14](issues/14-archive-the-confirmed-vault-image-without-requiring-unlock.md) |
| 17 | Busy selection change; settings failure/partial/success; view or destination editor closes | [15](issues/15-coordinate-vault-selection-and-closing-with-owned-operations.md) |
| 18 | Settings restore/default recovery or later verified reconciliation changes selected path | [15](issues/15-coordinate-vault-selection-and-closing-with-owned-operations.md) |
| 19 | Credential extraction races selection change or new restriction | [09](issues/09-own-shared-vault-unlock-and-credential-handoff-operations.md), [15](issues/15-coordinate-vault-selection-and-closing-with-owned-operations.md) |
| 20 | Failed/slow metadata subscriber and delayed old notification | [09](issues/09-own-shared-vault-unlock-and-credential-handoff-operations.md), [10](issues/10-manage-vault-entries-and-key-imports-through-owned-operations.md), [11](issues/11-change-the-vault-master-password-through-one-owned-operation.md), [15](issues/15-coordinate-vault-selection-and-closing-with-owned-operations.md) |
| 21 | Last window or editor closure with native/I/O work and with quiescent uncertainty; reopen/switch-back | [11](issues/11-change-the-vault-master-password-through-one-owned-operation.md), [12](issues/12-back-up-and-restore-vaults-through-complete-authenticated-operations.md), [13](issues/13-create-vaults-only-through-explicit-guarded-initialization.md), [14](issues/14-archive-the-confirmed-vault-image-without-requiring-unlock.md), [15](issues/15-coordinate-vault-selection-and-closing-with-owned-operations.md) |
| 22 | Concurrent reconciliation, a later pass racing owner release and retired IDs | [11](issues/11-change-the-vault-master-password-through-one-owned-operation.md), [12](issues/12-back-up-and-restore-vaults-through-complete-authenticated-operations.md), [13](issues/13-create-vaults-only-through-explicit-guarded-initialization.md), [14](issues/14-archive-the-confirmed-vault-image-without-requiring-unlock.md), [15](issues/15-coordinate-vault-selection-and-closing-with-owned-operations.md) |
| 23 | Capacity at/beyond each record, binding, consumer, subscriber, input, native, byte and artifact threshold | [09](issues/09-own-shared-vault-unlock-and-credential-handoff-operations.md), [10](issues/10-manage-vault-entries-and-key-imports-through-owned-operations.md), [11](issues/11-change-the-vault-master-password-through-one-owned-operation.md), [12](issues/12-back-up-and-restore-vaults-through-complete-authenticated-operations.md), [13](issues/13-create-vaults-only-through-explicit-guarded-initialization.md), [14](issues/14-archive-the-confirmed-vault-image-without-requiring-unlock.md), [15](issues/15-coordinate-vault-selection-and-closing-with-owned-operations.md) |

## Candidate 5

| Case | Accepted scenario | Owning draft tickets |
| --- | --- | --- |
| 1 | Admission rejection, invalid stopping offsets, and mutation of original input after acceptance | [16](issues/16-preserve-committed-ssh-packets-and-truthful-send-progress.md), [17](issues/17-run-sftp-through-shared-ssh-channel-operations.md) |
| 2 | Different local/peer IDs, windows and packet limits | [17](issues/17-run-sftp-through-shared-ssh-channel-operations.md) |
| 3 | Stop before packet commitment, during cipher/sequence advancement and during pending write | [16](issues/16-preserve-committed-ssh-packets-and-truthful-send-progress.md) |
| 4 | Small window/packet limits splitting terminal encoded units | [16](issues/16-preserve-committed-ssh-packets-and-truthful-send-progress.md), [18](issues/18-deliver-ssh-terminal-startup-and-interaction-through-the-shared-transport-and-channel-owners.md) |
| 5 | SFTP partial frame, zero send credit and concurrent Close | [16](issues/16-preserve-committed-ssh-packets-and-truthful-send-progress.md), [19](issues/19-preserve-sftp-outcomes-and-bounded-reopen-through-channel-shutdown.md) |
| 6 | SFTP mutation frame's final packet has an uncertain write outcome | [16](issues/16-preserve-committed-ssh-packets-and-truthful-send-progress.md), [19](issues/19-preserve-sftp-outcomes-and-bounded-reopen-through-channel-shutdown.md) |
| 7 | Queued RequestReply stop, committed request stop, Notify, and late replies | [16](issues/16-preserve-committed-ssh-packets-and-truthful-send-progress.md), [17](issues/17-run-sftp-through-shared-ssh-channel-operations.md) |
| 8 | Slow consumer while a request reply arrives, followed by continuing output | [17](issues/17-run-sftp-through-shared-ssh-channel-operations.md) |
| 9 | Early terminal DATA/stderr, shell success, delivery failure and attempt stop races | [18](issues/18-deliver-ssh-terminal-startup-and-interaction-through-the-shared-transport-and-channel-owners.md) |
| 10 | Inbound exit facts and unknown want-reply requests while consumption is paused | [17](issues/17-run-sftp-through-shared-ssh-channel-operations.md), [18](issues/18-deliver-ssh-terminal-startup-and-interaction-through-the-shared-transport-and-channel-owners.md) |
| 11 | One-byte/zero-byte DATA and interleaved extended data through the window limit | [17](issues/17-run-sftp-through-shared-ssh-channel-operations.md) |
| 12 | SFTP length-prefix/body splits, maximum body, invalid length and multiple frames per chunk | [17](issues/17-run-sftp-through-shared-ssh-channel-operations.md), [19](issues/19-preserve-sftp-outcomes-and-bounded-reopen-through-channel-shutdown.md) |
| 13 | Eight SFTP file requests plus browse/control with blocked local file work | [19](issues/19-preserve-sftp-outcomes-and-bounded-reopen-through-channel-shutdown.md) |
| 14 | WINDOW_ADJUST reserved, queued, committed, locally completed and uncertain | [17](issues/17-run-sftp-through-shared-ssh-channel-operations.md) |
| 15 | One zero-credit channel or blocked consumer during rekey | [16](issues/16-preserve-committed-ssh-packets-and-truthful-send-progress.md), [17](issues/17-run-sftp-through-shared-ssh-channel-operations.md), [19](issues/19-preserve-sftp-outcomes-and-bounded-reopen-through-channel-shutdown.md) |
| 16 | OPEN prevented versus committed then stopped; late confirmation/refusal | [17](issues/17-run-sftp-through-shared-ssh-channel-operations.md) |
| 17 | Never-arriving OPEN/CLOSE with repeated explicit SFTP reopen | [19](issues/19-preserve-sftp-outcomes-and-bounded-reopen-through-channel-shutdown.md) |
| 18 | Old close retires during a live reopen wait, versus after that wait ended | [19](issues/19-preserve-sftp-outcomes-and-bounded-reopen-through-channel-shutdown.md) |
| 19 | Directional EOF, reverse sending, exit status without EOF and CLOSE without EOF | [17](issues/17-run-sftp-through-shared-ssh-channel-operations.md), [18](issues/18-deliver-ssh-terminal-startup-and-interaction-through-the-shared-transport-and-channel-owners.md) |
| 20 | Simultaneous CLOSE with pending write, deferred control and payload callback | [17](issues/17-run-sftp-through-shared-ssh-channel-operations.md), [19](issues/19-preserve-sftp-outcomes-and-bounded-reopen-through-channel-shutdown.md) |
| 21 | Terminal Disconnect during valid incoming output and slow recording | [18](issues/18-deliver-ssh-terminal-startup-and-interaction-through-the-shared-transport-and-channel-owners.md) |
| 22 | Malformed SFTP/version/reply or local file failure versus malformed SSH/invalid ID/window overflow | [17](issues/17-run-sftp-through-shared-ssh-channel-operations.md), [19](issues/19-preserve-sftp-outcomes-and-bounded-reopen-through-channel-shutdown.md) |
| 23 | Consumer fails after partial application and rejects the final fact | [17](issues/17-run-sftp-through-shared-ssh-channel-operations.md) |
| 24 | Provider/parser buffers survive channel closure; pending file mutation and reopen | [19](issues/19-preserve-sftp-outcomes-and-bounded-reopen-through-channel-shutdown.md) |
| 25 | Old connection I/O/consumer/close completion arrives after a fresh generation | [19](issues/19-preserve-sftp-outcomes-and-bounded-reopen-through-channel-shutdown.md) |
| 26 | Every metadata/control/result/observer threshold and repeated wait cancellation/Close | [16](issues/16-preserve-committed-ssh-packets-and-truthful-send-progress.md), [17](issues/17-run-sftp-through-shared-ssh-channel-operations.md), [19](issues/19-preserve-sftp-outcomes-and-bounded-reopen-through-channel-shutdown.md) |
| 27 | Independent payload-credit vectors and real OpenSSH peer under tiny messages/stderr/exact exhaustion | [17](issues/17-run-sftp-through-shared-ssh-channel-operations.md), [19](issues/19-preserve-sftp-outcomes-and-bounded-reopen-through-channel-shutdown.md) |


## Candidate 6

| Case | Accepted scenario | Owning draft tickets |
| --- | --- | --- |
| 1 | Regenerate pinned data; change source hash/version/schema; corrupt a range/sequence | [20](issues/20-apply-pinned-grapheme-and-emoji-rules-to-received-terminal-output.md) |
| 2 | Official Unicode 17 GraphemeBreakTest corpus | [20](issues/20-apply-pinned-grapheme-and-emoji-rules-to-received-terminal-output.md) |
| 3 | Whole input, every single split position, and adversarial multi-part/page partitions for RI, Indic, Hangul, Prepend, combining, ZWJ and CRLF, including encoded-byte and surrogate-pair splits | [20](issues/20-apply-pinned-grapheme-and-emoji-rules-to-received-terminal-output.md), [22](issues/22-continue-long-received-graphemes-with-bounded-stored-spelling.md), [23](issues/23-analyze-paste-previews-through-owned-rust-input-plans.md) |
| 4 | Narrow, W/F and Ambiguous bases, with profile widths 1 and 2 | [20](issues/20-apply-pinned-grapheme-and-emoji-rules-to-received-terminal-output.md) |
| 5 | RGI sets, listed MQ/UQ multi-scalar forms and supported emoji-style pairs | [20](issues/20-apply-pinned-grapheme-and-emoji-rules-to-received-terminal-output.md) |
| 6 | Bare text-default symbol, its VS16 form, and text variation after VS16 | [20](issues/20-apply-pinned-grapheme-and-emoji-rules-to-received-terminal-output.md) |
| 7 | Allowed trailing decoration, interior marks, repeated selectors and later structure | [20](issues/20-apply-pinned-grapheme-and-emoji-rules-to-received-terminal-output.md), [22](issues/22-continue-long-received-graphemes-with-bounded-stored-spelling.md) |
| 8 | Unsupported ZWJ/modifier/tag/flag forms and isolated non-supported components | [20](issues/20-apply-pinned-grapheme-and-emoji-rules-to-received-terminal-output.md) |
| 9 | Recognized prefix becomes unsupported, then a complete supported sequence | [20](issues/20-apply-pinned-grapheme-and-emoji-rules-to-received-terminal-output.md) |
| 10 | 255, 256, 257 and much longer same-cluster continuations, including page/chunk crossings and supplementary scalars | [22](issues/22-continue-long-received-graphemes-with-bounded-stored-spelling.md) |
| 11 | Unpaired surrogate or invalid complete input on the last analysis page | [23](issues/23-analyze-paste-previews-through-owned-rust-input-plans.md), [24](issues/24-send-exact-long-grapheme-input-with-scalar-safe-stop.md) |
| 12 | Initial UTF-8 signature per source, interior/repeated FEFF and isolated marks | [20](issues/20-apply-pinned-grapheme-and-emoji-rules-to-received-terminal-output.md) |
| 13 | DATA/stderr/local echo, VT cutoff, independent REP/grid/Text tails and saturation | [22](issues/22-continue-long-received-graphemes-with-bounded-stored-spelling.md) |
| 14 | Right-edge width growth/shrink, wrap on/off, whole-cluster erase and resize/reflow | [20](issues/20-apply-pinned-grapheme-and-emoji-rules-to-received-terminal-output.md), [22](issues/22-continue-long-received-graphemes-with-bounded-stored-spelling.md) |
| 15 | Serialized Text continuation, retirement, TAB fidelity and concatenation of stored spans | [22](issues/22-continue-long-received-graphemes-with-bounded-stored-spelling.md) |
| 16 | Same complete finalized paste without a live terminal and in preview/send paths | [23](issues/23-analyze-paste-previews-through-owned-rust-input-plans.md), [24](issues/24-send-exact-long-grapheme-input-with-scalar-safe-stop.md) |
| 17 | Source exceeds its captured maximum, target's final-page unencodable suffix or unsuccessful reservation | [25](issues/25-admit-shared-large-pastes-within-existing-application-and-input-budgets.md), [26](issues/26-configure-large-clipboard-pastes-with-bounded-full-text-confirmation.md) |
| 18 | Shared paste sent to targets with different encodings, Ambiguous width and bracket modes | [25](issues/25-admit-shared-large-pastes-within-existing-application-and-input-budgets.md) |
| 19 | Reconnect, authority/mode/encoder-basis changes and reservation revalidation | [24](issues/24-send-exact-long-grapheme-input-with-scalar-safe-stop.md), [25](issues/25-admit-shared-large-pastes-within-existing-application-and-input-budgets.md) |
| 20 | Input begins with a continuation that extends an existing local-echo tail | [24](issues/24-send-exact-long-grapheme-input-with-scalar-safe-stop.md) |
| 21 | Stop within short/long graphemes, CRLF, multibyte/stateful units; partial/uncertain failure and cancellation | [24](issues/24-send-exact-long-grapheme-input-with-scalar-safe-stop.md) |
| 22 | VT-separated adjacent text, FEFF, wide cells and generated separators in snapshots | [21](issues/21-preserve-authoritative-stored-spans-in-rendering-uia-and-history.md) |
| 23 | Mutation/disposal after capture; mix metadata from another capture/generation/projection | [21](issues/21-preserve-authoritative-stored-spans-in-rendering-uia-and-history.md) |
| 24 | Parked normal history, active alternate screen, pure reflow, eviction and edit-then-restore | [21](issues/21-preserve-authoritative-stored-spans-in-rendering-uia-and-history.md) |
| 25 | AI response too small for the next maximum supported cluster; UIA full-result refusal | [21](issues/21-preserve-authoritative-stored-spans-in-rendering-uia-and-history.md) |
| 26 | Admitted paste beyond the former 1 MiB encoded limit; default 512 MiB/configured source-size boundaries; ASCII, Japanese, supplementary and legacy/stateful data; capture/compaction overlap | [25](issues/25-admit-shared-large-pastes-within-existing-application-and-input-budgets.md), [26](issues/26-configure-large-clipboard-pastes-with-bounded-full-text-confirmation.md) |
| 27 | Multiple windows, shared broadcast source, real copies, delayed release and old-generation completion | [25](issues/25-admit-shared-large-pastes-within-existing-application-and-input-budgets.md) |
| 28 | Admission rejection and unexpected execution failure with prior Raw/log admission | [20](issues/20-apply-pinned-grapheme-and-emoji-rules-to-received-terminal-output.md), [22](issues/22-continue-long-received-graphemes-with-bounded-stored-spelling.md) |
| 29 | Existing WPF/UIA, paste, recording, history, transport and responsiveness integration workloads | [21](issues/21-preserve-authoritative-stored-spans-in-rendering-uia-and-history.md), [25](issues/25-admit-shared-large-pastes-within-existing-application-and-input-budgets.md), [26](issues/26-configure-large-clipboard-pastes-with-bounded-full-text-confirmation.md) |
| 30 | Source below/equal/above 1,024 bytes; CR/LF independently present; configured maximum <=1 KiB | [26](issues/26-configure-large-clipboard-pastes-with-bounded-full-text-confirmation.md) |
| 31 | Ordinary input/automatic replies and target paste work compete for existing input capacity | [25](issues/25-admit-shared-large-pastes-within-existing-application-and-input-budgets.md) |
| 32 | Huge single line or grapheme in preview | [26](issues/26-configure-large-clipboard-pastes-with-bounded-full-text-confirmation.md) |
| 33 | Existing broadcast deadline expires during final target validation or just before commitment | [25](issues/25-admit-shared-large-pastes-within-existing-application-and-input-budgets.md) |
| 34 | Heart + VS16 + 254 acute accents + VS15, compared with 253 accents | [22](issues/22-continue-long-received-graphemes-with-bounded-stored-spelling.md) |
| 35 | Woman + 254 acute accents + ZWJ + laptop + grinning face | [22](issues/22-continue-long-received-graphemes-with-bounded-stored-spelling.md) |
| 36 | Giant outgoing grapheme and local-echo storage saturation | [24](issues/24-send-exact-long-grapheme-input-with-scalar-safe-stop.md) |
| 37 | Terminal state and paste ownership approach the shared 4 GiB application envelope; multiple windows and shared versus real copied storage | [25](issues/25-admit-shared-large-pastes-within-existing-application-and-input-budgets.md) |
| 38 | Capture/copy overlap, later terminal growth, competing work and cancellation with delayed cleanup | [25](issues/25-admit-shared-large-pastes-within-existing-application-and-input-budgets.md) |
| 39 | Old settings, positive/invalid byte values, Save/Cancel/failure and limit changes during capture/preview/send | [26](issues/26-configure-large-clipboard-pastes-with-bounded-full-text-confirmation.md) |
