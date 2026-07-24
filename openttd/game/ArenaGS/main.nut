class ArenaGS extends GSController {
    function Save() {
        return {};
    }

    function Load(version, data) {
        /* Loading a fixed starting save must be observable while it remains paused. */
        GSLog.Info("ARENA_PHASE02_GAMESCRIPT_READY");
    }

    function Start() {
        while (true) {
            /* This fixed lifecycle heartbeat is read only from the dedicated server console. */
            GSLog.Info("ARENA_PHASE02_GAMESCRIPT_READY");
            this.Sleep(74);
        }
    }
}
