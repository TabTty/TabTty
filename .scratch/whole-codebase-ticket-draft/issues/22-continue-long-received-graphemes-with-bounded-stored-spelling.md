## Parent

Whole-codebase ownership implementation plan (new map issue; identifier assigned on publication).

## What to build

Keep a terminal session running when a received grapheme exceeds 256 Unicode scalars, retain its first 256 scalars for stored terminal content and correctly recognize the following grapheme across all terminal and recording projections.

Related existing work: [#18](https://github.com/TabTty/TabTty/issues/18), [#31](https://github.com/TabTty/TabTty/issues/31), [#32](https://github.com/TabTty/TabTty/issues/32). These references identify established behavior or adjacent acceptance work; they are not blocking dependencies and are not to be reopened or reimplemented by this ticket.

## Acceptance criteria

- [ ] Retain exactly the first 256 Unicode scalars of each stored spelling while bounded complete-stream segmentation state advances over every eligible omitted continuation. Preserve the next real boundary without an artificial split, unbounded accumulator, disconnect, new warning or incomplete-recording result solely for normal omission.
- [ ] Keep source/REP, each editable grid/projection tail and serialized Text-position state independent. Saturation of one owner must not return early from the source operation or suppress another projection or eligible recording stream. Admission and execution start from their own valid state.
- [ ] Use only retained spelling for width and drawing eligibility. Preserve all intermediate geometry changes through scalar 256; later omitted scalars cannot change geometry. Verify 255/256/257, supplementary scalars, and long RI/Indic/ZWJ/combining continuations across chunks.
- [ ] Render, copy, UIA, retained history/search and normal-screen receive-only Text logs expose the retained spelling. Alternate-screen Text, Terminal and Raw retain their eligible original input under existing recording rules. REP replays its retained source spelling; no synthetic separators or omission characters enter content.
- [ ] Preserve whole-cluster erase, wrapping, reflow and stable retained anchors using authoritative stored spans. Serialized Text position stays bounded and independent; do not replay an emitted prefix or emit TAB without proven faithful expansion.
- [ ] Include the heart + VS16 + 254 accents + VS15 case versus 253 accents, and woman + 254 accents + ZWJ + laptop + grinning face. Confirm omitted VS15 cannot shrink geometry and omitted laptop still advances break state so the final face starts a new stored span.
- [ ] Preserve real admission, allocation, I/O and invariant failures and their existing narrow/affected-session outcomes. Earlier Raw/log admission is not rolled back by a later failure; no managed fallback is introduced.

## Blocked by

- Draft ticket 21: Preserve authoritative stored spans in rendering, UIA and history.
