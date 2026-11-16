# TabTty TDD seams

S1 was confirmed by the user's instruction to continue after its proposal.

## S1. Terminal output

The terminal output module accepts received terminal payload bytes and their
source. Callers can read a snapshot of the resulting terminal rows. The same
interface is used by the session and by tests. See the
[session and recording contract](session-and-recording.md).

This seam catches errors in incremental decoding and visible terminal state.
Network negotiation, log file writes, and WPF rendering need separate seams.

The first test delivers the UTF-8 bytes `E3 81` and then `82` from the same
source. The snapshot after the first chunk is empty and the snapshot after
the second contains exactly `あ`. The test uses the terminal snapshot as the
observable result and the literal character as its expected value.

The tests exercise the same terminal output interface that session callers use.

The current implementation covers UTF-8 decoding, independent receive sources,
initial UTF-8 signatures, receive EOF, and a fixed-size screen with retained
scrollback. Tests exercise CR, LF, BS, HT, VT, FF, delayed wrap, row retirement,
CUP/HVP, relative cursor movement, line erasure, screen and scrollback erasure,
and removal of OSC/DCS payloads from the displayed text. Escape parser state
is kept per receive source. The model currently measures columns in Unicode
scalars. Cluster widths, further VT operations, legacy encodings, and recording
are subsequent stages.

Run the dependency-free .NET test executable with
`dotnet run --project tests/TabTty.Core.Tests`. A failed check returns a
nonzero exit code.
