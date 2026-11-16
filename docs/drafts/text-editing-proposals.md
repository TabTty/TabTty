# Confirmed normal-screen Text editing decisions

> Consolidated on 2026-11-10: [implementation readiness and completed contracts](../specification/implementation-readiness.md). Accepted product decisions below remain binding; historical lists of unfinished technical design are superseded by those contracts. Implementation and runtime verification have not begun.

Status: Q149-Q151 accepted on 2026-11-09, with Q150 confirmed after clarification
of normal-screen cursor movement and complex Bash editing. Originally prepared 2026-11-08
after Q146-Q148 acceptance. Specification only; no implementation, prototype,
runtime test, or performance measurement is authorized.

Read with `connection-profiles-issue.md`, `receive-logging-issue.md`, Q106 in
`resize-and-vault-proposals.md`, and `retention-and-log-io-proposals.md`.
These choices concern the receive-only normal-screen Text projection. Raw byte
fidelity, Terminal operation recording, and alternate-screen sanitized streaming
are unchanged. Examples assume recording is active and all described content
is eligible; interval eligibility never follows merely from being on screen.

## Q149: Apply destructive edits without first saving the removed text (accepted)

Apply overwrite, erase, character/line deletion, and insertion
truncation to unfinished Text state without a pre-edit snapshot. A remote erase
or delete is an edit, not a received newline or an instruction to archive the
text it removes. Preserve any surviving meaningful text and layout spacing under
the existing TAB/space rules. Unused or erased trailing cells do not become
explicit received spaces.

For example, received `draft`, CR, erase-to-end-of-line, `done`, CRLF records
`done\r\n`, not `draftdone\r\n`. If unfinished `draft` is completely erased and
the session then closes, no `draft` is recorded. A genuine LF/CRLF after erasure
can still finalize an empty line; erasure alone does not generate a blank record.

Already written log contents remain append-only. Clearing a screen or saved
history cannot erase the file, begin a new part, or add a clear-screen marker.
If a previously recorded row is revised and later finalized, preserve Q57's
changed-version rule; do not append snapshots of each intermediate edit.
Retain the distinction between deleting/erasing content and retiring output
because of a capacity limit. Q106/Q146's pre-retirement flush remains accepted.

This applies only to content the supported control actually removes. Do not
erase scrollback merely because a screen-only erase was received. Exact control
variants, reset semantics, and remote saved-history-clear support belong to the
terminal capability contract; this question does not silently select them.

## Q150: Cursor positioning is not line finalization (accepted)

Moving or restoring the cursor, changing margins, or changing attributes does
not finalize Text or manufacture a newline. Keep unfinished
contents editable at their retained locations. At a received LF/CRLF, finalize
the eligible logical output line being left, including its surviving soft-wrap
continuations; do not finalize every row crossed by earlier cursor movements.
Preserve the received boundary form and the existing recorded-prefix/revision
rules. Positioning alone does not establish a soft-wrap relationship.

Append records in finalization-event order, not a guessed order of remote writes
or an attempt to reorder the existing log into screen order. If row 1 contains
unfinished `A`, cursor positioning writes `B` on row 2, and row 2 receives CRLF,
`B\r\n` is recorded first. `A` remains pending until its own valid finalization
or an eligible flush/retirement boundary. The log cannot later insert `A` ahead
of the already recorded `B`.

At pause, receive EOF, disconnect, orderly close, or normal-to-alternate switch,
flush the remaining eligible, changed/unrecorded contents in retained buffer
order (oldest/top to newest/bottom), independent of viewport scrolling and the
cursor's final position. Keep joined soft-wrap continuations together and omit
already recorded unchanged ranges. Do not dump untouched blank rows or the full
screen. Later close after EOF must not duplicate the flush.

Preserve the accepted no-added-newline tail rule. Consequently, `A` at row 1 and
`B` at row 2 created only by cursor positioning, with no received newline, can
become `AB` at a final flush. Do not insert blank lines for skipped screen rows,
screen-position labels, or invisible separators. A finalized `B\r\n` followed
by the still-pending `A` at disconnect instead yields `B\r\nA`.
This limitation concerns cursor-addressed normal-screen programs; ordinary
received newlines and local-soft-wrap suppression retain their accepted behavior.
Text is not a spatial screen dump or a guarantee to reconstruct the display.

The user's final answer accepts imperfect Text results during complex editing,
including shell commands containing backslash-escaped newlines. Continue using
received LF/CRLF as the logging boundary without special-case interpretation of
cursor positioning. Do not parse shell grammar, recognize command acceptance,
delay records until a guessed prompt, or infer backslash continuation to repair
the transcript. Cursor/edit controls still affect the receive-only terminal
projection; this does not change Text into a concatenated stripped input stream.
Keep existing CR/BS/erase handling, soft-wrap suppression, genuine CRLF/LF
preservation, recording eligibility, and append-only revision rules.

Q57 already requires comparison with the same retained output's recorded version,
not global string deduplication. Scrolling/reflow preserves surviving identity;
separate newly produced rows with the same text remain separate output. Color,
selection, search, and highlight-only changes are not Text revisions. Exact
logical-line splitting/merging and partially recorded prefix cases remain a
technical provenance contract, not permission to equate rows by text alone.

### Explanation requested on 2026-11-09

The cursor here is the terminal's output insertion position, controlled by the
remote application's received instructions. It is not the user's mouse pointer.
An explicit position instruction can place the next character at row 2, column 1
without sending a line-feed character. The examples start with an empty normal
screen, sufficient width/height, no local echo, and active eligible recording.
They describe accepted Text results, not measured application behavior.

| Received event | Normal screen | Text state |
| --- | --- | --- |
| Printable `A` at row 1, column 1 | Row 1: `A` | `A` is pending; nothing finalized |
| Set cursor to row 2, column 1; printable `B` | Row 1: `A`; row 2: `B` | Both remain pending |
| Receive CRLF after `B` | Row 1: `A`; row 2: `B`; cursor on row 3 | Append `B\r\n`; `A` remains pending |
| Disconnect | Same retained text | Append remaining `A`; total `B\r\nA` |

If the third step is omitted and the connection instead ends immediately after
`B`, the final flush appends `A` then `B` without an invented separator:
`AB`. If the peer instead sends ordinary `A\r\nB\r\n`, both lines are finalized
in sequence and the result is `A\r\nB\r\n`.

These surprising results are a consequence of combining per-line newline
finalization, append-only storage, and no synthetic tail separators. They are
not an unavoidable terminal limitation. The user subsequently accepted this
tradeoff and explicitly allowed imperfect complex-edit transcripts. No attempt
to infer the remote shell's semantic command boundaries is required.

### Applicability outside the alternate screen (2026-11-09)

The user asked whether ordinary Bash use actually exhibits these operations.
Cursor movement and alternate-buffer selection are independent controls;
normal-screen programs can position the cursor without switching buffers.
This establishes possibility, not how often a particular application does it.

The reviewed GNU Readline source uses terminal cursor-up controls in
`_rl_move_vert` when editing/redrawing input spanning screen rows. Downward
movement in that function instead emits LF and CR. `_rl_update_final` moves to
the bottom display row and normally emits CRLF. Thus normal-screen input editing
does involve vertical movement, but it does not establish the Q150 `B\r\nA`
outcome in ordinary Bash use. That example is hypothetical, not a measured trace.

Design implication: received LF alone is not proof that the user accepted a
command, since redisplay can emit LF too. Preserve the distinction between a
logging boundary and shell input acceptance. The user's subsequent Q150 answer
accepts this limitation without adding shell-specific repair. Later validation
must still check the agreed terminal editing/wrapping/newline behavior; permission
for imperfect transcripts is not permission for incorrect display semantics.
No runtime test was performed.

- [Microsoft cursor positioning and separate alternate-buffer selection](https://learn.microsoft.com/en-us/windows/console/console-virtual-terminal-sequences)
- [Bash command-line editing](https://www.gnu.org/software/bash/manual/html_node/Command-Line-Editing.html)
- [GNU Readline redisplay source, reviewed blob 803d4cebadba81cf1c792da1bf6a6c726497c83f](https://gnu.googlesource.com/binutils-gdb/+/master/readline/readline/display.c)

## Q151: Preserve eligible text displaced out of retention by scrolling (accepted)

Treat received output scrolling as movement of existing output.
If a row enters retained scrollback, do not record it merely for leaving the
active screen. If scrolling actually removes it from all retained Text state,
submit its eligible, changed/unrecorded contents immediately before removal,
without adding a newline. This extends the accepted quota-retirement rule to
output discarded at a scrolling-region edge or by reverse scrolling.

Apply this to actual scroll operations, including scrolling caused by supported
index/reverse-index operations. Keep explicit erase/delete and insertion
truncation under Q149's edit policy. Distinguish operations by terminal semantics,
not by guessing whether the remote application is a shell, editor, or progress
display. A margin change or a cursor movement that does not actually scroll
does not invoke this rule.

For multiple rows displaced by one operation, use their pre-operation retained
order from top to bottom, preserving surviving soft-wrap order and already
recorded ranges. A genuine LF/CRLF that also scrolls must not record the same
content twice or gain a second line ending. Scroll controls alone are not LF.
Blank rows created by scrolling do not produce log records until eligible
received content or a genuine newline provides something to record.

For example, an unfinished `status` row leaving a partial scrolling region with
no retained-history destination is saved as `status`, without LF. Deleting the
same unfinished row explicitly follows Q149 and does not save its removed text.
This means repeated scroll-driven updates can leave intermediate values in Text;
ordinary same-row CR/erase progress editing still retains only its final value.

User viewport scrolling, hiding a tab, moving a window, and browsing history are
not received output scrolling and never trigger this policy. Paused/excluded
content is not made eligible; a writer unable to admit the retirement snapshot
reports the accepted logging failure rather than silently claiming it was saved.

## Evidence and remaining technical work

Microsoft's VT reference distinguishes cursor positioning, scroll operations,
and text modification. Positioning does not itself scroll; reverse index may
scroll at a margin. Insert/delete-line operations are constrained by margins
and can discard content. These display effects do not prescribe Text logging;
Q149-Q151 are accepted TabTty product policies.

- [Microsoft VT cursor, scroll, and text-editing semantics](https://learn.microsoft.com/en-us/windows/console/console-virtual-terminal-sequences)

Tera Term 5.7.1's ordinary text logging is incremental character logging with
filtering, not this edited-screen snapshot model. Its initial screen-buffer dump
is a separate option. Therefore it cannot directly resolve TabTty's accepted
Text finalization policies. This finding comes from static source/manual review,
not a runtime comparison.

- [Tera Term 5.7.1 logging options](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/doc/en/html/menu/file-log.html)
- [Tera Term 5.7.1 parsed-character logging](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/vtterm.c)
- [Tera Term 5.7.1 log serialization](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/filesys_log.cpp)

Do not assume identical ED3 semantics across reference terminals: Tera Term's
enabled ED3 path invokes ClearBuffer, affecting the entire retained buffer and
cursor/margins, while Microsoft's saved-line erase retains the active page.
Also distinguish LF from control operations such as IND/NEL/VT/FF that can
perform line movement. Their supported display behavior and Text boundary
mapping must be specified explicitly; no implicit LF equivalence is accepted
by this round. Microsoft documentation and implementation also differ in the
wording of SU/SD's affected range; the exact control matrix needs source review.

- [Tera Term 5.7.1 buffer operations](https://github.com/TeraTermProject/teraterm/blob/v5.7.1/teraterm/teraterm/buffer.c)
- [Microsoft terminal control implementation](https://github.com/microsoft/terminal/blob/main/src/terminal/adapter/adaptDispatch.cpp)
- [Tera Term control-function reference](https://teratermproject.github.io/manual/5/en/about/ctrlseq.html)

Complete the supported-control matrix, logical-line identities after nonlinear
edits, already-recorded prefixes, moved/split/merged content, per-source boundary
provenance, interval exclusions, and bounded snapshot ownership. Define later
acceptance examples for these operations across CRLF chunks, reflow, quota
retirement, pause/resume, EOF, and write failure. Q149-Q151 examples express
accepted expected results. None are tests
that have been run. Implementation readiness has not
yet been reached.
