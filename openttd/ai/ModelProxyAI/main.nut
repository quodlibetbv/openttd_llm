class ModelProxyAI extends AIController {
    function Save() {
        return {};
    }

    function Load(version, data) {
        /* Loading a fixed starting save must be observable while it remains paused. */
        AILog.Info("ARENA_PHASE02_MODEL_PROXY_READY");
    }

    function Start() {
        while (true) {
            /* This fixed lifecycle heartbeat does not perform a company action. */
            AILog.Info("ARENA_PHASE02_MODEL_PROXY_READY");
            this.Sleep(74);
        }
    }
}
