// AI agents (MCP): live notice when an agent asks for confirmation.
(function () {
  "use strict";
  if (window.sbMcpLoaded) return;
  window.sbMcpLoaded = true;

  document.addEventListener("sb:live", function (e) {
    var detail = e.detail || {};
    if (detail.type !== "agent_action.pending") return;
    var payload = detail.payload || {};

    document.querySelectorAll("[data-mcp-indicator]").forEach(function (el) {
      var count = (parseInt(el.getAttribute("data-count"), 10) || 0) + 1;
      el.setAttribute("data-count", String(count));
      var label = el.querySelector("[data-mcp-count]");
      if (label) label.textContent = String(count);
      el.hidden = false;
    });

    if (document.querySelector("[data-mcp-reload-on-pending]")) {
      window.location.reload();
      return;
    }
    if (window.sb && window.sb.toast) {
      window.sb.toast((payload.client || "An AI agent") + " is waiting for your approval. Open Agent actions to review it.");
    }
  });
})();
