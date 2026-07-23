# Sanitized Replay Fixtures

Replay fixtures are deterministic test inputs, not captured provider transcripts. They may contain only the common decision contract, an expected observation hash, and synthetic usage values.

Do not add request headers, credentials, raw provider payloads, hidden reasoning, account identifiers, or production observations. Use public summaries and fictional identifiers only. The root secret scanner and fixture test enforce this boundary.
