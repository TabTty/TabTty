## Parent

Whole-codebase ownership implementation plan (new map issue; identifier assigned on publication).

## What to build

Render received terminal output using one Rust-owned Unicode rule implementation with the accepted emoji recognition, width transitions and independent source state. Exercise the shared rules through actual terminal admission, execution and presentation rather than introducing an unused utility.

Related existing work: [#18](https://github.com/TabTty/TabTty/issues/18), [#38](https://github.com/TabTty/TabTty/issues/38). These references identify established behavior or adjacent acceptance work; they are not blocking dependencies and are not to be reopened or reimplemented by this ticket.

## Acceptance criteria

- [ ] Pin Unicode 17.0.0, UAX29 r47, UAX11 r44 and Emoji 17 / UTS51 r29. Supply reproducible generation/checking, exact versioned source hashes and schema checks, property defaults and range expansion, and required Unicode notices. Missing, corrupt or incompatible native code/data fails explicitly; no runtime download, OS-data substitution or new third-party dependency.
- [ ] Recognize the accepted union of RGI sequences, emoji-style variation pairs and listed multi-scalar minimally/unqualified emoji spellings. Bare text-default singletons remain ordinary. Preserve VS15 priority and exact full-core lookup after removing only the permitted maximal trailing decoration whose General_Category is Mn/Mc/Me and whose Grapheme_Cluster_Break is Extend/SpacingMark, excluding the complete Variation_Selector property and U+20E3. Unsupported structures use ordinary width without recognizing a substring.
- [ ] Use shared definitions with independent admission/execution, source/REP, grid and projection state. Preserve CRLF, FEFF/signature handling, isolated-mark one-cell behavior, source separation and VT continuation cutoffs. An arbitrary input chunk does not finalize a grapheme.
- [ ] Observe every intermediate width growth or shrink during a receive operation, including right-edge wrap, whole-cluster erase and reflow, with independently expected cells, revisions and recording content. Shaping does not decide terminal width and the dotted circle remains drawing-only.
- [ ] Verify the official pinned GraphemeBreakTest corpus and independent width/membership vectors through the production terminal path, including all relevant splits. Include supported variation pairs outside RGI-only lookup and unsupported prefix/interior-decoration cases; neither the old managed algorithm nor generated production tables serve as the expected-result oracle.
- [ ] Preserve no-prefix native admission rejection, independently admitted Raw/log outcomes and the existing failure scope after execution starts. Expand the shared implementation beside only the existing managed consumers, then migrate those consumers in later tickets and delete their production rule implementation when its final consumer moves. Add no fallback, engine selection or new duplicate authority. Each slice keeps existing functional checks green; the complete amended contract is accepted only when all seven slices pass.

## Blocked by

None (can start immediately).
