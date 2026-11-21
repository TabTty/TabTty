# Separate log destination confirmation from profile trust

Application defaults and macro connection arguments can change the effective
log destination while the connection profile stays identical. Remember profile
trust by normalized file location and the SHA-256 of the exact profile bytes so
one approved profile version can serve those connections. Recording preparation
checks the resolved destination and asks for separate approval when a previously
confirmed drive mapping has changed. This preserves review of redirected log
output. A recording start failure blocks transport startup or closes an already
established connection, including on resume or retry.

[Log filename template redesign](../drafts/receive-logging-issue.md#log-filename-template-redesign)
