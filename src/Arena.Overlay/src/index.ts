import { OverlaySnapshot } from "./state.js";

function setText(element: Element | null, value: string): void {
  if (element !== null) {
    element.textContent = value;
  }
}

export function renderSnapshot(root: ParentNode, snapshot: OverlaySnapshot): void {
  setText(root.querySelector("[data-arena-provider]"), snapshot.provider);
  setText(root.querySelector("[data-arena-model]"), snapshot.model);
  setText(root.querySelector("[data-arena-game-date]"), snapshot.gameDate);
  setText(root.querySelector("[data-arena-summary]"), snapshot.publicSummary);
  setText(root.querySelector("[data-arena-status]"), snapshot.executionStatus);
  setText(root.querySelector("[data-arena-score]"), snapshot.score?.toString() ?? "—");
  const connectionRoot = root instanceof Document
    ? root.documentElement
    : root instanceof Element
      ? root
      : root.querySelector("[data-arena-root]");
  connectionRoot?.setAttribute(
      "data-arena-connection",
      snapshot.isConnected ? "connected" : "stale",
  );
}
