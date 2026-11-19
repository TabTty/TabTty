## Parent

Whole-codebase ownership implementation plan (new map issue; identifier assigned on publication).

## What to build

Unlock the selected vault once through complete operations shared by windows and editors, and deliver exact eligible credentials without caller-managed unlock lifetimes or stale-context reuse.

## Acceptance criteria

- [ ] Introduce named complete unlock operations on non-secret file-bound contexts, with fixed opened location and role epoch, synchronous bounded admission, operation identity, monotonic observations and coalesced current metadata. Contexts and subscriptions never own roots or prolong their lifetime; same-ID copies remain distinct locations.
- [ ] Track actual unlock consumers independently from observers. Cancelling observation leaves the consumer interested; explicit stop removes only that consumer. Retire an attempt when its last consumer leaves while retaining native work, secret buffers and expensive-worker charge until actual completion; late success cannot join or adopt into a fresh attempt.
- [ ] Bind typed input replies to the operation, request, source image and live host. Handle host transfer, duplicate/late replies and interaction failure with one cleanup owner and no automatic prompt loop. Do not occupy the expensive worker while awaiting human input or hold reentrant locks across callbacks.
- [ ] Ordinary Unlock, authentication, credential pickers and Explorer editors never initialize missing or empty vaults. Preserve existing independent cryptographic validation, selected-owner application-run unlock lifetime and editor-role lifetime without a plaintext-entry cache.
- [ ] Resolve only the requested exact vault/entry/kind and recheck selection epoch, revision and restrictions immediately before authentication handoff. Clear invalidated results instead of retargeting or retrying; already handed-off credentials follow the existing authentication lifetime and established sessions remain connected.
- [ ] Use real unlock/credential paths and independent invalid/valid encrypted fixtures to verify shared windows, observation-versus-consumer cancellation, last-consumer/native completion races, host loss, A-to-B-to-A contexts, exact-reference refusal, worker/buffer bounds and cleanup without secret diagnostics.

## Blocked by

None (can start immediately).
