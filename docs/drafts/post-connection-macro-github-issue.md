# Run a profile's embedded post-connection macro in the connected tab

## User outcome

A user can open a profile, wait for a Linux or switch prompt, and arrive at a terminal ready for manual work. A single `.ttty` carries the editable steps. A recipient supplies the named password entries in their selected vault. Use the ordinary Connect action for SSH, Telnet and serial COM. This feature belongs to macro-enabled builds.

The maintainer selected two integration behaviors: an external `Connect` waits for the embedded body to complete, and the existing profile trust decision covers the inspectable source. The detailed language table, receive-cursor handoff and failure behavior below are implementation defaults for this handoff.

## Connection and execution

- Capture the body with the profile version approved for this attempt. Attach the existing macro receive observer before terminal data can arrive. Start exactly once when the terminal is usable: after SSH shell/PTY startup, after Telnet transport readiness, or after the COM port has opened and been configured. The first wait can match startup data already received. Telnet/COM do not have a remote-login-completed event. The author decides what to wait for and whether to send an initial Enter.
- Apply this to ordinary profile opens, including file activation and multi-profile opens, and to each successful explicit reconnect. Each tab owns its run. A failed or cancelled connection sends nothing. A disconnect ends the old run. Reconnect reads and trusts the profile again and starts a fresh body from its first statement. Moving a connected tab preserves the existing run.
- When an external `.tttym` calls `Connect`, execute the embedded body first. `Connect` returns successfully to the caller **only after the body completes successfully**. Both phases use the same tab, transport, recording and exclusive input ownership. Give the body its own local variables. Hand the existing receive cursor to the caller on success, including any unconsumed suffix and responses that arrived during the handoff. A prompt already consumed by the body stays consumed. With an empty body, `Connect` returns at terminal readiness with the initial receive history available.
- If embedded execution times out, fails, or is stopped, end the automation, including a waiting external macro. Report the failing phase, source location where available and reason. Keep a healthy terminal connected for manual use after send cleanup. Reuse the existing pause, stop, input ownership, history-loss, alternate-screen and recording rules. Never retry an uncertain send. On disconnect cancel the continuation. No second Connect or recording setup is involved.

## Embedded source and portability

- Store the editable source in the profile. A current-format profile with an empty body connects normally. TabTty is unreleased, and the connection profile and vault data formats may change incompatibly. Reading or migrating prerelease files, feature markers for older readers, and compatibility tests are outside the requirements. Keep existing profile and macro resource limits.
- The editor takes a statement body. Store its language version in the profile format so the user can start at line 1 with a statement or comment. Reuse the ordinary macro parser and its meanings. This is a closed subset for both execution and editor assistance.

| Area | Embedded v1 |
| --- | --- |
| Statements | Comments, blocks, initialized locals (`var` or an admitted explicit type), call statements, `if` / `else if` / `else`, and `return;`. |
| Values and conditions | String, boolean and integer literals, local references, `WaitPattern`, `WaitMatch.AlternativeName`, parentheses, equality/inequality and boolean conditions. Locals can hold a reusable pattern, timeout or wait result. Keep ordinary and verbatim string escaping. |
| Output | `WriteLine(text)` with the existing optional `markReceiveBoundary`, and `WritePasswordLine()` or `WritePasswordLine(name)`. Use the profile's Enter and encoding settings. |
| Waits | `Literal`, `Regex`, `WaitPrompt(pattern, timeoutMs)`, and `WaitAnyPrompt([name: pattern, ...], timeoutMs)`. Both waits require a finite timeout in milliseconds. Preserve named alternatives, unfinished prompt matching and send-boundary semantics. |

`Connect`, recording overrides, `#constants`, `Macro` metadata and invocation arguments, path/window/dialog functions, loops, helpers, exception handling, lists and general data processing, delays, completed-line waits and sends without Enter are outside this subset. An unsupported construct anywhere in the body, including an unexecuted branch, is a source error. Keep the full language available for external macros.

## Trust and passwords

- Include the embedded body in **profile trust**. Let the user identify that the profile sends automatic input and inspect the full, inert body when reviewing a connection or importing/registering a version that will become trusted. An import alone never executes it. Use the exact reviewed bytes and the existing location/version rules, including Connect once and verified editor saves. Ensure the trust decision covers the executable body in the resulting profile version. Opening the profile editor, checking the body and inserting a template are side-effect free.
- External macro launch approval continues to apply to the external file. The embedded body runs under the approved profile snapshot. Preserve ordinary host-key checks, vault unlock, and separate log-destination decisions.
- Use exact password entry names in the selected vault. `WritePasswordLine()` uses the saved password reference captured from the profile. Telnet/COM have no default password binding and use `WritePasswordLine("entry name")`. Resolve the current password when sending. Missing entries and unlock cancellation fail visibly. Preserve the existing Telnet/COM password notice and diagnostic secret handling. Templates store only references. Compiling or editing a profile requires no vault access.

## Profile editor

- Add **Post-connection macro** / **接続後マクロ** to the left navigation and use the same text for the right-side field heading. Use the Japanese description **接続後、このタブで自動的に実行するマクロです。** and the English description **This macro runs automatically in this tab after connection.** Under it, place a compact insert menu and a multiline code field that grows and shrinks to fill the remaining space. Preserve ordinary editor save/cancel behavior and both UI themes.
- **Insert routine** / **定型処理を挿入** offers three editable snippets: run one command per line in sequence, Linux `sudo -i`, and switch `enable`. The first takes a prompt and commands and waits for the prompt between sends. The privilege snippets take normal/privileged prompts and a password-request pattern and send a vault password only in the password branch. Offer the profile password where available or an explicit vault entry name. Properly escape supplied literals. Insert at the caret, preserve surrounding text, and leave the resulting source editable. Make snippets composable, including when a snippet already consumed the prompt needed by the next one. In particular, composing the sequence and `enable` must work with a device that emits each prompt only once.
- Provide keyboard-accessible **completion and argument hints** as the user types, including in unfinished calls. Suggest only constructs valid in this subset and in the current position. Use the existing shared macro analysis. Provide **live inline diagnostics** that underline offending text and show a localized explanation and line/column below the field. Clear stale diagnostics after correction. Check the complete body on save and before connecting from an externally edited file. A source error prevents saving an invalid draft or starting that connection. Preserve the draft for correction. Runtime wait and credential errors remain runtime results.

## Acceptance checks

1. Move one `.ttty` to another PC with the named vault entries and no companion macro file. After the normal review, it runs. Review a changed body, open once, import without connecting, and cancel a review. Check that clearing the body restores ordinary connection behavior in the current format.
2. Exercise `sudo -i` both with a password request and with immediate arrival at the configured root prompt. Exercise `show clock`, `terminal length 0`, then `enable` with and without a password request. Check command order, one send per step, and that a prompt from before a marked send cannot satisfy its subsequent wait.
3. Exercise SSH early terminal output, a Telnet `login:` arriving at connection startup, and a quiet COM peer that only responds to an explicitly scripted Enter. Confirm timeout, missing password, Stop, disconnect, reconnect and tab movement behavior.
4. From an external macro, assert wire order: embedded steps finish, then the first caller command is sent. Verify the receive cursor at handoff, separate local variables, one tab and one recording. On embedded failure or Stop, verify no caller command is sent and a healthy terminal accepts manual input. Verify that an empty-body profile allows the external caller to proceed at terminal readiness.
5. Exercise template composition, snippet values containing quotes or backslashes, completion and signature help on partial input, disallowed syntax in a dead branch, and corrected diagnostics. Check both UI languages, resize the editor, and save/reopen the profile. Update the English and Japanese user references with one working example.

## Existing contracts

Use the repository's `docs/drafts/macro-language.md`, `docs/drafts/macro-specification.md`, `docs/drafts/macro-validation.md`, `docs/specification/profile-trust.md`, `docs/specification/persistence-and-windows.md`, and `docs/adr/0010-share-macro-parser-with-vs-code.md`. Relevant completed work includes #3, #75, #111, #114, #119 and #126. Update the affected contracts to describe embedded execution and the later successful return from external `Connect` when a body is present.
