# ArenaGS Foundation Package

`ArenaGS` is the authoritative game-side boundary. It performs no game actions in Phase 02. It emits a fixed readiness heartbeat to the dedicated-server console and persists an empty state table so its `Load` callback can emit the same marker while the fixed starting save is still paused. Phase 03 adds the versioned AdminPort dispatcher.
