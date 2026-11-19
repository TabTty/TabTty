## Parent

Whole-codebase ownership implementation plan (new map issue; identifier assigned on publication).

## What to build

Use the same immutable Rust-derived input plan for actual paste preview counts and complete source validation, without requiring a live terminal or exposing scalar-loop and page-management protocols to callers.

Related existing work: [#12](https://github.com/TabTty/TabTty/issues/12), [#19](https://github.com/TabTty/TabTty/issues/19). These references identify established behavior or adjacent acceptance work; they are not blocking dependencies and are not to be reopened or reimplemented by this ticket.

## Acceptance criteria

- [ ] Provide complete source analysis through the existing paste/input seam and use it in the real preview/counting path. Return immutable source identity, grapheme/newline facts and bounded traversal; ordinary callers do not manage a Unicode cursor, reservation recipe or internal page protocol.
- [ ] Validate the entire exact finalized input, including the final page, before success. Unpaired surrogates and forbidden controls reject the whole not-yet-started operation without replacement, normalization, live encoder progress, payload or echo. Carry surrogate and segmentation context over internal page boundaries.
- [ ] Traverse giant graphemes using offsets and bounded continuation state rather than collecting another whole-grapheme buffer or requiring a descriptor per scalar/grapheme. The input plan is never the display-retention prefix; source and plan allocation belongs to the existing input/preview owner and remains charged until release.
- [ ] Produce identical source counts and semantic units without a live terminal and through the actual preview path. Target encoding, current mode, authority and local-echo continuation remain separate target preparation obligations.
- [ ] Use an expand/migrate approach that keeps currently supported paste behavior green until the sender migration is complete. Keep enlarged send eligibility behind the existing admission boundary until the exact-streaming ticket lands; no preview-only acceptance may let an unsupported sender emit a prefix. Remove the temporary compatibility boundary with that migration.
- [ ] Verify empty/basic/newline input, split surrogates, RI/Indic/ZWJ/combining sequences, giant-grapheme analysis and invalid final-page input through the complete operation. Retain the existing broadcast preview owner and late-answer cancellation semantics.

## Blocked by

- Draft ticket 20: Apply pinned grapheme and emoji rules to received terminal output.
