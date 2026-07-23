class ModelProxyAI extends AIController {
    function Start() {
        /* The proxy must never construct, buy, sell, borrow, or alter orders. */
        while (true) {
            this.Sleep(74);
        }
    }
}
