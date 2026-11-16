# Issue tracker: GitHub

Issues and specifications live in GitHub Issues for `TabTty/TabTty`.
Use the GitHub CLI (`gh`) for operations.

## Conventions

- Before writing, confirm the target with `gh repo view --json nameWithOwner`.
- The target must be `TabTty/TabTty`. Stop if a different repository is selected.
- Create with `gh issue create`, read with `gh issue view`,
  list with `gh issue list`, and update with `gh issue edit`.
- Save multiline bodies to a UTF-8 temporary file and pass it with `--body-file`.
- Comment with `gh issue comment` and close with `gh issue close`.
  Record the resolution before closing an issue.
- Use the label names and meanings in `triage-labels.md`.
- "Publish to the issue tracker" means create a GitHub issue.
- "Fetch the relevant ticket" means read the issue body, labels, and comments.

## Parent relationships and dependencies

- Use GitHub sub-issues for parent relationships.
  Use `--parent` on creation or `--add-sub-issue` on an existing parent issue.
- Set dependencies with `--blocked-by` or `--add-blocked-by`.
- If these features are unavailable, add `Part of #<number>` or
  `Blocked by: #<number>` at the top of the issue body.
- An issue is unblocked when every blocker is closed.

## Pull requests as a triage surface

PRs as a request surface: no.

Issues and pull requests share a number space. Resolve the item type before
acting on a bare number.

## Wayfinding

- Record the overall plan in an issue labelled `wayfinder:map`.
- Link child tickets as sub-issues and label them by type:
  `wayfinder:research`, `wayfinder:prototype`, `wayfinder:grilling`,
  or `wayfinder:task`.
- Select open, unassigned, unblocked child tickets in plan order.
- Assign the ticket to the driving developer when work starts.
- On resolution, comment with the result, close the ticket, and add the
  conclusion and a link to the overall plan.
