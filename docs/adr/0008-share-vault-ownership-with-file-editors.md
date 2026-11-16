---
status: accepted
---

# Share selected vault ownership with the standalone editor

Host Explorer-launched file editors in the same per-user TabTty instance as
terminal windows. For the selected vault, the editor, management tabs, and
connections share one unlocked owner and its committed revisions. This avoids
the stale-snapshot failure that a separate editor process would cause on save.

An editor for another vault holds its unlock for the lifetime of that editor.
Changing the selected vault transfers the role to an existing unlocked owner
when available, while each standalone editor retains its opened path.

See the [Explorer and editor contract](../specification/persistence-and-windows.md#file-explorer-and-editors)
and the [vault ownership contract](../specification/vault-format.md#secret-owners-and-cleanup).
