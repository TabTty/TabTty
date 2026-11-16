---
status: accepted
---

# Reuse the terminal tab's SSH connection for SFTP

Open an on-demand SFTP subsystem on a separate SSH session channel within the
invoking terminal tab's authenticated connection. This lets the user transfer
files under the same SSH identity without a second login or retaining the
credentials used for the terminal login. Keep the terminal and SFTP payloads
separate, including their flow control, close handling, and user-visible output.

A second SSH connection would avoid a server's per-connection session limit but
would require an independent login and give the transfer a separate lifecycle.
When the server refuses the extra channel or the SFTP subsystem, report the
refusal in the transfer window. An ordinary refusal leaves the terminal usable.
