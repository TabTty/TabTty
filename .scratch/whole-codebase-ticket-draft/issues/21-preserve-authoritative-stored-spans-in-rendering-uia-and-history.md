## Parent

Whole-codebase ownership implementation plan (new map issue; identifier assigned on publication).

## What to build

Make copy, rendering, UIA navigation and AI screen/history observations consume owned native text and the exact stored spans that produced it, so exported concatenation cannot invent or merge terminal characters.

Related existing work: [#31](https://github.com/TabTty/TabTty/issues/31), [#32](https://github.com/TabTty/TabTty/issues/32). These references identify established behavior or adjacent acceptance work; they are not blocking dependencies and are not to be reopened or reimplemented by this ticket.

## Acceptance criteria

- [ ] Extend the existing owned observation path with text, authoritative stored spans, explicit offset units and required cell/drawing-aid facts bound to one capture, session generation, projection and range. Do not reconstruct segmentation from exported strings or pair text with metadata from another result.
- [ ] Preserve VT-separated adjacent spans, zero-cell FEFF content, wide-cell continuations, generated separators and soft-wrap relationships. WPF consumes native drawing-aid eligibility instead of runtime Unicode categories; aids remain absent from text, copy and offsets.
- [ ] Keep combined active screen, parked combined normal history and the contracted UIA active projection distinct. Copy, UIA character movement and AI pages/search consume those real projections without a mutable managed replica.
- [ ] Owned results remain readable after mutation or disposal, while live operations still validate generation, projection, stable anchors and their existing authority/content/retention evidence. Preserve UIA collapse on eviction, AI error precedence, edit-then-restore detection, pure-reflow anchors and unchanged-prefix survival.
- [ ] UIA full-result requests return the complete requested retained text or explicitly fail. AI retains its external page limits; insufficient capacity for the next complete stored span returns resource_limit without truncation, false progress or an empty-success loop. Account result ownership until actual release.
- [ ] Verify real rendering/copy/UIA/history/search calls under VT separation, mutation/disposal, reflow, alternate screens, eviction and maximum stored-span capacity. Reuse the existing observation foundation rather than rebuilding the work described by the related issues.

## Blocked by

- Draft ticket 20: Apply pinned grapheme and emoji rules to received terminal output.
