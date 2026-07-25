# Sanitized Provider Fixtures

Replay fixtures are deterministic test inputs, not captured provider transcripts. The synthetic DeepSeek completion fixture exercises the adapter's OpenAI-compatible envelope while containing only fictional IDs, common public decision fields, and synthetic usage values.

Do not add request headers, credentials, raw provider payloads, hidden reasoning, account identifiers, or production observations. Use public summaries and fictional identifiers only. The root secret scanner and fixture tests enforce this boundary.
