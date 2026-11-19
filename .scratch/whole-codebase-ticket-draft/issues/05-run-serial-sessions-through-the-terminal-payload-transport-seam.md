## Parent

Whole-codebase ownership implementation plan (new map issue; identifier assigned on publication).

## What to build

Migrate the live serial session to the complete transport operations introduced for Telnet. A successfully configured COM port produces ordered payload and lifecycle facts through the same session consumer, while serial line control, BREAK and device-overrun behavior remain dedicated serial responsibilities.

## Acceptance criteria

- [ ] Bind the session consumer before opening the port and keep required recording preparation before device I/O. Report readiness only after the configured COM port opens successfully; do not substitute a port or setting, infer remote login or add automatic reconnection.
- [ ] Route serial receive, encoded input submission and disconnect through the common complete operations with the same source/order, complete-prefix/uncertainty and observation-versus-cancellation distinctions. Geometry reports that serial requires no wire notification rather than pretending to send one.
- [ ] Preserve the selected flow-control and DTR/RTS ownership rules and dedicated BREAK operation, including its existing duration and cleanup attempt. Do not add a generic capabilities framework or change saved profile settings during temporary line operations.
- [ ] Bound pending reads, payload, metadata, sends and late cleanup under existing accounting. Use supported flow control where it can recover, report device overrun explicitly, and never claim lossless backpressure or silently discard received payload.
- [ ] Preserve received output and separately owned recording finalization across EOF/disconnect, cancelled waits, delayed port open/read/write/close and replacement generations. Actual device-resource release remains distinct from operation completion and visual removal.
- [ ] Exercise the ordinary session/Rust/recording path with controlled serial adapters for partial writes, blocked consumers, overrun and late cancellation, and retain actual COM/device integration for driver behavior. Remove the serial caller's obsolete raw-wire recipe after migration.

## Blocked by

- Draft ticket 04: Run Telnet sessions through complete terminal-payload transport operations.
