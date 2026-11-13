"use strict";
(() => {
  const state = window.__autumnResourceRepeat;
  const script = document.currentScript;
  if (!state || state.status !== "running" || !script || state.activeScript !== script ||
      script.dataset.expected !== String(state.next) || state.executions !== state.next - 1 ||
      script.dataset.executed) {
    throw new Error("RESOURCE_REPEAT_EXECUTION_ORDER");
  }
  state.executions++;
  script.dataset.executed = "native-resource-repeat-v1";
})();
