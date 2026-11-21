# Confirmed profile and initial log-file preparation

> Consolidated on 2026-11-10: [implementation readiness and completed contracts](../specification/implementation-readiness.md). Accepted product decisions below remain binding; historical lists of unfinished technical design are superseded by those contracts. Implementation and runtime verification have not begun.

Status: Q158 accepted as recommended on 2026-11-10. Specification only;
no implementation, prototype, runtime file-I/O
experiment, installation, or publication is authorized.

Read with `connection-profiles-issue.md`, `profile-management-proposals.md`,
`connection-attempt-proposals.md`, `receive-logging-issue.md`, and
`retention-and-log-io-proposals.md`. Preserve external-profile trust, required
file-backed connections, and existing log/error behavior.

## Already accepted boundaries

- Read a fresh bounded, interpretable UTF-8 JSON `.ttty` snapshot on explicit
  connect/reconnect. Required types/fields, supported version, and ambiguous
  values are basic loading checks; detailed consistency validation remains in
  the settings editor. Do not add a profile approval gate.
- Preserve unknown optional data on save, or report that safe round-tripping is
  unavailable. Do not silently strip behavior or repair destinations/credentials.
- Ordinary relative log paths resolve against the opened profile's directory,
  never the application's current directory. Existing connections retain their
  captured destination. A moved profile is a new registration under Q110.
- Prepare the selected log directory and open every selected log file before
  starting the transport. Initial preparation failure prevents that connection;
  failure after connection startup follows the accepted recording-error policy.
- Apply the [log collision policy](receive-logging-issue.md#log-filename-template-redesign)
  to existing files. Each new recording part admits only its own eligible data.
  Reconnection is explicit. Other sessions and windows remain usable.

These are not new product questions. Exact path identity, unsupported path forms,
schema fields, and parser/resource limits remain technical specification work.

## Q158: Visible, cancellable preparation with two fixed deadlines

Use two separate fixed 30-second application deadlines:

1. Loading the profile: path/file access and bounded reading/parsing needed to
   obtain the connection snapshot.
2. Preparing initial recording: resolving/preparing the selected directory and
   opening all selected Raw/Text/Terminal files as one stage. Do not give each
   format an additional 30 seconds. Skip this stage when recording is disabled.

Start each deadline when TabTty begins executing that stage. Waiting behind an
existing modal dialog or for an explicit user choice is not file-preparation
time. File/cloud/network-provider waiting during an issued stage does count.
Do not restart the budget for each metadata call, read chunk, directory, file,
or partial progress. Keep the exact generation/cutoff order in the connection
attempt contract. There is no additional user-configurable timeout initially.

Show the active stage and a Cancel action without blocking the UI. On user
cancellation or expiry, invalidate the attempt, report cancelled/timed out as
appropriate, and do not start its transport. If a connecting tab already exists,
keep it with its failure/cancellation state under the established lifecycle;
otherwise show an activation/open error without replacing an existing session.
Do not add a second cancellation confirmation.

An explicit retry starts a fresh attempt and rereads the profile. It must not
reuse an old successful result that arrived after cancellation or silently
continue in the background. No automatic retry or fallback to another profile,
folder, saved credential, or recording-disabled connection is introduced.

These deadlines precede and are separate from Q131's profile-configured transport
connection budget. They also do not replace Q147's active-write stall handling
or Q148's explicit waiting choice during log closure. This is initial preparation,
not permission to abandon an already chosen close-drain wait after 30 seconds.

## OS completion and resource ownership

Deadline expiry ends the application's acceptance of the attempt; it does not
guarantee that Windows, a storage driver, or a network/cloud provider has stopped
the underlying open/read/create call. Request cancellation where available and
observe actual completion before releasing/reusing owned buffers and handles.
A timeout wrapper around a task is not proof that its underlying work stopped.

Keep late results bound to the cancelled attempt. A late file open must be
closed safely, never start a session or become a new attempt's log handle.
Directories or empty files created before or after a timeout can remain; do not
promise an all-or-nothing filesystem transaction or guess that a named file is
safe to delete. Preserve the existing no-automatic-log-deletion policy.

Bound outstanding and cancelled preparation work, handles, and worker ownership.
If cleanup capacity is exhausted, report that a new attempt cannot start yet
instead of spawning unbounded background work. Do not block unrelated terminal
I/O or kill unrelated windows to cancel a stuck operation. Exact counts, worker
or helper isolation, application-exit cleanup, and cancellation race handling
remain technical contracts before implementation readiness.

## Path facts to preserve in detailed design

Windows distinguishes fully qualified `C:\Logs` and `\\server\share\Logs` from
drive-relative `C:Logs` and current-drive-root-relative `\Logs`. Do not silently
treat every rooted string as fully qualified or every nonqualified string as an
ordinary profile-relative path. Explicit-base path resolution is available;
environment-variable expansion is a separate operation and is not implied by
calling a path-normalization API. Q158 does not decide new expansion syntax or
change the accepted profile/log locations.

Cloud placeholders may hydrate during ordinary file access, so a local-looking
path is not proof that opening it cannot involve a provider/network wait.
These observations justify bounded preparation, not an automatic refusal of
explicitly selected files merely because their storage is remote or synchronized.

## Primary evidence reviewed on 2026-11-09

- [Microsoft I/O cancellation and responsive-worker examples](https://learn.microsoft.com/en-us/windows/win32/fileio/canceling-pending-i-o-operations)
- [.NET Task.WaitAsync contract](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.task.waitasync?view=net-10.0)
- [Cloud file placeholder hydration](https://learn.microsoft.com/en-us/windows/win32/cfapi/build-a-cloud-file-sync-engine)
- [Windows path formats](https://learn.microsoft.com/en-us/dotnet/standard/io/file-path-formats)
- [Explicit-base path resolution](https://learn.microsoft.com/en-us/dotnet/api/system.io.path.getfullpath?view=net-10.0)
- [Explicit environment-variable expansion](https://learn.microsoft.com/en-us/dotnet/api/system.environment.expandenvironmentvariables?view=net-10.0)

The two 30-second values are accepted TabTty product limits, not Windows
defaults or measured response times. Later authorized validation must include
slow/offline shares, cloud placeholders, cancellation before/after open completion,
late creates, repeated retries, modal waiting, and unrelated-session responsiveness.
No such tests have been run. Initial implementation readiness has not yet been reached.
