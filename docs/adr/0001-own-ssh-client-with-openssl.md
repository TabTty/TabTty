---
status: accepted
---

# Implement the SSH client with OpenSSL cryptography

TabTty will implement its SSH client protocol and session layer in C#, using
OpenSSL for cryptographic operations. This gives TabTty direct ownership of
terminal-channel bytes, selected character encodings, and PTY control without
depending on a modified ssh.exe or an external complete SSH client library.
The project accepts responsibility for SSH protocol correctness, interoperability,
and protocol security maintenance in addition to maintaining its OpenSSL dependency.

## Consequences

- The dependency policy permits official OpenSSL libraries in place of the
  earlier OpenSSH client dependency.
- OpenSSL supplies cryptographic primitives, not a ready-made SSH client.
- An agreed, narrowly scoped exception permits the OpenSSH/OpenBSD
  `bcrypt_pbkdf` implementation and required Blowfish/helper source for encrypted
  OpenSSH private-key files, because OpenSSL does not supply this KDF. This
  preserves support for those key files while adding responsibility for the
  adopted source and its notices; it does not adopt or modify ssh.exe.
- RLogin is an architectural reference; this decision does not adopt its source
  code or dependencies.
- This is an accepted design decision, not authorization to implement or prototype.
