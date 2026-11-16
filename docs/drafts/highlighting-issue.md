# Define regular-expression highlighting for terminal output

> Consolidated on 2026-11-10: [implementation readiness and completed contracts](../specification/implementation-readiness.md). Accepted product decisions below remain binding; historical lists of unfinished technical design are superseded by those contracts. Implementation and runtime verification have not begun.

Working issue-body draft for the GitHub issue tracker. The specification interview
is ongoing; this draft does not authorize implementation or prototyping.

Q121-Q124 in `highlighting-details-proposals.md` now confirm detailed regex
semantics, shared editing and invalid-rule handling, text-change scheduling,
and overload behavior. These are accepted requirements alongside the rules below.
Q119 cursor behavior is accepted in `terminal-display-proposals.md` with no
user-selectable cursor options in the initial release.

## Confirmed requirements

Users can define highlight rules by pairing a regular expression with a display
style. Highlighting affects terminal presentation only. It does not change raw
or text log contents, the text available to future macros, or the timing of
receive processing. Macros are deferred beyond the initial release.

### Evaluation timing

- Schedule highlighting independently for each tab after output has been idle
  for approximately 500 ms.
- When switching tabs or scrolling, evaluate the visible area without waiting
  for text to become idle. Automatic scrolling that follows output uses the
  normal idle timing.
- Display received text and hand it to receive logging without waiting for
  highlighting.
- Reevaluate added or changed lines instead of rescanning the entire history.
  Prioritize the currently visible area.
- Define activity by changes to matchable displayed text, including enabled
  local echo, overwrite, and erase. Pure cursor/color/control traffic does not
  restart the idle timer. Remove invalidated ranges and reject stale results.
- Evaluate the complete affected line so matches can span separate receive
  chunks, such as `ERR` followed by `OR`.
- Keep regex evaluation off the UI interaction path and impose regex timeouts.
- Defer highlight evaluation in non-visible tabs. On showing a tab, evaluate
  the visible area first. Reception, terminal state updates, and enabled logging
  continue while that tab is hidden. Future macros must also remain independent
  of tab visibility.

### Matching scope

- In the initial release, apply highlighting to the normal screen only. Disable
  it while an alternate screen is active, based on terminal screen state rather
  than the name of the remote application.
- Match each logical output line separately; initial support excludes matches
  spanning newline boundaries.
- Treat wrapping caused by viewport width as part of the same logical line.
- Include incomplete lines, such as prompts without a trailing newline.
- Style only the matched portion. A separate whole-line styling mode is outside
  the initial scope. A display cluster is indivisible: matching part of a combining
  sequence or emoji styles that cluster, without extending to the whole line.
- Use standard .NET regular expressions, with case-sensitive matching by
  default and a per-rule option to ignore case. Use invariant culture and normal
  inline-option precedence; preserve lookarounds/backreferences. Ignore zero-length
  matches for drawing, but count them toward evaluation work limits.
- Match edited normal-screen logical text, including local echo, with tab/layout
  gaps represented as spaces and unused trailing padding omitted. Soft wraps
  introduce no newline. Do not match raw bytes, controls, or log representations.
- Apply one style to the entire match. Initial support excludes separate styles
  for capture groups.

### Display style and precedence

- Support foreground color, background color, bold, and underline. Each
  attribute can be left unchanged.
- Use the same highlight style in Light and Dark application themes; do not
  provide theme-specific highlight variants. The terminal's default background
  is fixed black and its default foreground is fixed white, with no user setting
  to change those defaults. Application themes affect surrounding UI instead.
- Honor received ANSI foreground and background color selections. Apply
  highlighting over the resulting terminal attributes. Resetting a foreground
  or background color restores its corresponding fixed default.
- Allow users to reorder rules. At each character, the first applicable rule in
  the list wins.
- Apply only that winning rule at the character. Unspecified attributes inherit
  the terminal's original presentation, not attributes from lower-priority
  highlight rules.
- Selection and search apply foreground/background overlays above regex rules
  (Q77 accepted). The color precedence is selection, search match, winning regex
  rule, then terminal attributes/fixed defaults. This does not merge regex rules
  or change their first-match precedence.
- Use blue background/white text for selection and yellow background/black text
  for search matches, with an outline for the current search result even where
  it overlaps selection. These styles are shared across Light and Dark themes.
- Removing an overlay restores the current underlying attributes, including
  reevaluated regex styling. Overlays do not modify rule definitions, received
  text, copied text, or logs. See `terminal-interaction-issue.md`.

### Rule management and existing output

- Store rules in named, reusable highlight rule sets selectable in connection
  profiles.
- Offer a no-highlighting choice.
- Start with no enabled rule set. Use explicit Save/Cancel for rule edits;
  saved shared-set changes affect all tabs using it. Save invalid patterns only
  while disabled. Duplicate a set for independent profile-specific changes.
- Apply rule edits to existing output as well as future output. Reevaluate the
  visible area first and evaluate off-screen scrollback when it becomes visible.
- Changing rules does not modify existing log files.

### Errors and timeouts

- Identify invalid regular expressions in the settings UI.
- On an evaluation timeout, suspend the affected rule in that tab and notify
  the user without repeated notifications for the same suspension.
- Continue receiving, normal display, and logging while a rule is suspended.
- Let the user fix the rule or explicitly resume it.
- Apply accepted limits of 100 rules/set, 4,096 UTF-16 units/pattern, 65,536
  units/logical line, and 4,096 enumerated matches/line across rules. Overlong or
  excessively matching lines receive no regex overlay for that revision, with a
  quiet status and no change to display text/copy/logging. Do not publish partial
  rule precedence or repeatedly reevaluate the unchanged skipped revision.
- Use an explicit approximate 50 ms search timeout and approximately 20 ms
  cooperative background slices. A slice exhaustion yields work; it does not
  suspend a rule. Changing another rule does not silently resume a timed-out rule.

## Decisions still to resolve

- Exact overlay colors and cursor contrast/control/IME details within the
  accepted fixed cursor/focus behavior; selection/search color precedence and
  current-result outline are already agreed. No cursor choice UI is pending.
- Total worker/cache memory, cross-tab scheduling fairness, cancellation/failure
  transitions, and later authorized performance verification of accepted limits.
- Exact shared-set storage/reference schema and missing-resource UI details.

## Acceptance scenarios agreed so far

- A prompt without a newline can be highlighted after the idle delay.
- Continuous output receives periodic highlighting opportunities without
  waiting indefinitely for an idle period.
- A match split across receive chunks is recognized after reevaluation.
- Viewport wrapping does not break a match within a logical output line.
- Highlight rule changes do not alter logging or the underlying text available
  to future macro matching.
- Display clusters outside a regex match retain their original presentation
  unless covered by another match. A partial-cluster match styles the cluster.
- Overlapping rules follow list order and do not combine attributes from
  different rules at the same character.
- A rule evaluation timeout leaves reception, normal display, and logging
  operational and can be recovered from explicitly.
- Switching the application between Light and Dark changes neither terminal
  default colors nor highlight styles.
- Received ANSI colors remain visible where no highlight rule overrides them;
  resetting those colors restores the fixed terminal defaults.
- A hidden tab continues receiving, updating terminal state, and enabled logging
  without evaluating highlights until it becomes visible.
- An application using an alternate screen retains its own terminal styling
  without regex highlighting. Highlighting applies again on the normal screen.
- Selection colors take precedence over search-match colors and regex styling;
  search colors take precedence over regex styling. The current-result outline
  stays distinguishable when overlapping selection. Removing overlays restores
  the underlying style without changing copied text or log contents.
