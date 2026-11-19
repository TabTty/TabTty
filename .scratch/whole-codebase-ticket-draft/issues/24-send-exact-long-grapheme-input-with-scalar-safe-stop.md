## Parent

Whole-codebase ownership implementation plan (new map issue; identifier assigned on publication).

## What to build

Send the complete original paste through the existing session writer, including graphemes longer than 256 scalars, while Stop remains responsive at valid encoding boundaries and local echo reflects only definitely submitted content.

Related existing work: [#12](https://github.com/TabTty/TabTty/issues/12), [#19](https://github.com/TabTty/TabTty/issues/19). These references identify established behavior or adjacent acceptance work; they are not blocking dependencies and are not to be reopened or reimplemented by this ticket.

## Acceptance criteria

- [ ] Migrate actual paste sending to owned Rust input plans and target-derived encoded units; remove the temporary sender eligibility boundary. With observation consumers already migrated, remove the remaining production managed terminal grapheme rules and unused data when this final consumer moves. Never apply stored-prefix omission to outbound input. Keep C# ownership of authority, ordered input, pacing, transport and broadcast coordination.
- [ ] Preflight complete target encoding/finalization without advancing live state before first send. Keep source identity, current target generation, mode, encoder/configuration and authority valid until ordered use; target-neutral analysis does not bypass local-echo continuation admission.
- [ ] Normally successful transmission preserves exact input even for one giant grapheme. Encode and send in bounded work chunks without treating a chunk as a completed grapheme or adding pacing delays. No 256-scalar input rejection remains.
- [ ] An explicit Stop may end at the next complete Unicode scalar or required larger indivisible codec/control unit inside either a short or long grapheme. Keep CRLF indivisible and never intentionally cut a multibyte character or stateful control unit.
- [ ] Only definitely submitted complete units advance encoder and local-echo commitment. Reuse the existing transport seam: previously successful complete submissions are definite, unattempted work is unsent, and a failed attempted unit is conservatively uncertain unless the transport supplies finer authoritative evidence. Never infer successful bytes from an exception, replay uncertain bytes or require a separate transport refactor merely to obtain finer progress. Perform healthy reserved codec/bracket closure once under the existing ordered writer and automatic-reply rules.
- [ ] Local echo uses the bounded stored-spelling contract while the successful outbound payload remains complete. Preserve actual source/chunk ownership until workers and transport operations release it, including cancelled waits and old-generation completion.
- [ ] Verify exact bytes, counts, pacing, echo, short/long-cluster Stop, CRLF, legacy/stateful encodings, partial/uncertain writes and healthy closure through actual session/input/transport paths. Keep the AI input authority and 256 KiB UTF-8 limit unchanged.
- [ ] After observation and input consumers have migrated, remove the remaining production managed grapheme/width/drawing-category rule implementation and compatibility shims. Preserve unrelated protocol or diagnostic character-safety checks; no new managed fallback is introduced.

## Blocked by

- Draft ticket 22: Continue long received graphemes with bounded stored spelling.
- Draft ticket 23: Analyze paste previews through owned Rust input plans.
