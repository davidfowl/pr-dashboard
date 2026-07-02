function escapeHtml(value) {
    return String(value ?? "")
        .replaceAll("&", "&amp;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;")
        .replaceAll('"', "&quot;")
        .replaceAll("'", "&#39;");
}

export function renderHtml(instanceId, openInput) {
    const initialUrl = escapeHtml(openInput?.url ?? "");
    const canvasTitle = escapeHtml(openInput?.title ?? "Deployed instance manager");
    const safeInstanceId = escapeHtml(instanceId);

    return `<!doctype html>
<html>
  <head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <title>${canvasTitle}</title>
    <style>
      :root {
        color-scheme: dark light;
        --ops-bg: var(--background-color-default, #0f0d1d);
        --ops-surface: color-mix(in srgb, var(--background-color-default, #0f0d1d) 88%, var(--text-color-default, #ffffff) 5%);
        --ops-surface-raised: color-mix(in srgb, var(--background-color-default, #0f0d1d) 82%, var(--text-color-default, #ffffff) 8%);
        --ops-surface-muted: color-mix(in srgb, var(--background-color-default, #0f0d1d) 92%, var(--color-focus-outline, #7455dd) 8%);
        --ops-control: color-mix(in srgb, var(--background-color-default, #0f0d1d) 90%, var(--text-color-default, #ffffff) 7%);
        --ops-border: var(--border-color-default, rgba(185, 170, 238, 0.22));
        --ops-border-strong: color-mix(in srgb, var(--border-color-default, rgba(185, 170, 238, 0.22)) 70%, var(--text-color-muted, #b9aaee));
        --ops-text: var(--text-color-default, #ffffff);
        --ops-muted: var(--text-color-muted, #b9aaee);
        --ops-accent: var(--color-focus-outline, #7455dd);
        --ops-accent-soft: color-mix(in srgb, var(--color-focus-outline, #7455dd) 18%, transparent);
        --ops-success: #2eb67d;
        --ops-warning: #d29922;
        --ops-danger: #f85149;
        --ops-info: #58a6ff;
        --ops-log-bg: #0d1117;
        --ops-log-text: #c9d1d9;
        --ops-radius-sm: 8px;
        --ops-radius-md: 12px;
        --ops-radius-lg: 16px;
        --ops-shadow: 0 18px 56px rgba(0, 0, 0, 0.24);
        --ops-ease: cubic-bezier(0.16, 1, 0.3, 1);
      }

      * { box-sizing: border-box; }

      body {
        margin: 0;
        min-width: 320px;
        background:
          radial-gradient(circle at 12% -10%, color-mix(in srgb, var(--ops-accent) 20%, transparent), transparent 28rem),
          radial-gradient(circle at 90% 8%, rgba(46, 182, 125, 0.1), transparent 22rem),
          var(--ops-bg);
        color: var(--ops-text);
        font-family: var(--font-sans, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif);
        font-size: var(--text-body-medium, 14px);
        line-height: var(--leading-body-medium, 20px);
      }

      main {
        width: min(100%, 1240px);
        margin: 0 auto;
        padding: 20px;
      }

      h1, h2, p { margin-top: 0; }

      h1 {
        margin-bottom: 4px;
        font-size: 24px;
        line-height: 30px;
        font-weight: var(--font-weight-semibold, 650);
        letter-spacing: -0.02em;
        text-wrap: balance;
      }

      h2 {
        margin-bottom: 8px;
        font-size: 15px;
        line-height: 21px;
        font-weight: var(--font-weight-semibold, 650);
      }

      button, input, select {
        min-height: 38px;
        border: 1px solid var(--ops-border);
        border-radius: var(--ops-radius-sm);
        padding: 8px 10px;
        background: var(--ops-control);
        color: var(--ops-text);
        font: inherit;
      }

      input::placeholder { color: color-mix(in srgb, var(--ops-muted) 82%, var(--ops-text)); }

      button {
        display: inline-flex;
        align-items: center;
        justify-content: center;
        gap: 6px;
        cursor: pointer;
        font-weight: var(--font-weight-semibold, 600);
        transition:
          border-color 160ms var(--ops-ease),
          background-color 160ms var(--ops-ease),
          color 160ms var(--ops-ease),
          box-shadow 160ms var(--ops-ease),
          transform 160ms var(--ops-ease);
      }

      button:hover:not(:disabled) {
        border-color: var(--ops-accent);
        background: var(--ops-accent-soft);
      }

      button:active:not(:disabled) { transform: translateY(1px); }

      button:disabled {
        cursor: wait;
        opacity: 0.58;
      }

      input:focus-visible, select:focus-visible, button:focus-visible {
        outline: 2px solid var(--ops-accent);
        outline-offset: 2px;
      }

      code {
        border: 1px solid var(--ops-border);
        border-radius: 6px;
        padding: 1px 5px;
        background: var(--ops-surface-muted);
        font-family: var(--font-mono, "SFMono-Regular", Consolas, "Liberation Mono", monospace);
        font-size: var(--text-code-inline, 12px);
      }

      .muted { color: var(--ops-muted); }

      .topbar {
        display: flex;
        align-items: flex-start;
        justify-content: space-between;
        gap: 16px;
        margin-bottom: 16px;
      }

      .title-row {
        display: flex;
        align-items: center;
        gap: 10px;
        flex-wrap: wrap;
      }

      .lede {
        max-width: 68ch;
        margin-bottom: 0;
        color: var(--ops-muted);
        text-wrap: pretty;
      }

      .status-stack {
        display: grid;
        justify-items: end;
        gap: 6px;
        min-width: max-content;
      }

      .badge {
        display: inline-flex;
        align-items: center;
        gap: 6px;
        min-height: 24px;
        border: 1px solid var(--ops-border);
        border-radius: 999px;
        padding: 2px 9px;
        color: var(--ops-muted);
        background: var(--ops-surface-muted);
        font-size: 12px;
        font-weight: var(--font-weight-semibold, 600);
        white-space: nowrap;
      }

      .badge::before {
        content: "";
        width: 7px;
        height: 7px;
        border-radius: 999px;
        background: currentColor;
      }

      .badge.ok {
        color: var(--ops-success);
        border-color: color-mix(in srgb, var(--ops-success) 50%, transparent);
        background: color-mix(in srgb, var(--ops-success) 13%, transparent);
      }

      .badge.fail {
        color: var(--ops-danger);
        border-color: color-mix(in srgb, var(--ops-danger) 50%, transparent);
        background: color-mix(in srgb, var(--ops-danger) 13%, transparent);
      }

      .badge.warn {
        color: var(--ops-warning);
        border-color: color-mix(in srgb, var(--ops-warning) 52%, transparent);
        background: color-mix(in srgb, var(--ops-warning) 14%, transparent);
      }

      .connection-panel,
      .panel,
      .logs-panel {
        border: 1px solid var(--ops-border);
        border-radius: var(--ops-radius-lg);
        background: var(--ops-surface);
        box-shadow: var(--ops-shadow);
      }

      .connection-panel {
        display: grid;
        gap: 12px;
        margin-bottom: 14px;
        padding: 14px;
      }

      .connection-copy {
        display: flex;
        align-items: baseline;
        justify-content: space-between;
        gap: 12px;
        flex-wrap: wrap;
      }

      .connection-copy p { margin-bottom: 0; }

      .input-row {
        display: grid;
        grid-template-columns: minmax(260px, 1fr) auto auto;
        gap: 8px;
        align-items: end;
      }

      .field {
        display: grid;
        gap: 6px;
      }

      .field label {
        color: var(--ops-muted);
        font-size: 12px;
        font-weight: var(--font-weight-semibold, 600);
      }

      #baseUrl {
        width: 100%;
        font-family: var(--font-mono, "SFMono-Regular", Consolas, "Liberation Mono", monospace);
        font-size: 13px;
      }

      .button-primary {
        border-color: color-mix(in srgb, var(--ops-accent) 70%, var(--ops-border));
        background: var(--ops-accent);
        color: var(--color-white, #ffffff);
        box-shadow: 0 10px 26px color-mix(in srgb, var(--ops-accent) 26%, transparent);
      }

      .button-primary:hover:not(:disabled) {
        background: color-mix(in srgb, var(--ops-accent) 86%, var(--ops-text));
        color: var(--color-white, #ffffff);
      }

      .button-danger {
        border-color: color-mix(in srgb, var(--ops-danger) 58%, var(--ops-border));
        color: var(--ops-danger);
      }

      .button-danger:hover:not(:disabled) {
        background: color-mix(in srgb, var(--ops-danger) 14%, transparent);
      }

      .summary-grid {
        display: grid;
        grid-template-columns: repeat(3, minmax(0, 1fr));
        gap: 12px;
      }

      .panel {
        min-height: 148px;
        padding: 14px;
      }

      .panel-header {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 10px;
        margin-bottom: 10px;
      }

      .panel-header h2 { margin-bottom: 0; }

      .kv {
        display: grid;
        grid-template-columns: 118px minmax(0, 1fr);
        gap: 7px 10px;
        align-items: baseline;
      }

      .kv .key {
        color: var(--ops-muted);
        font-size: 12px;
      }

      .kv .value {
        min-width: 0;
        overflow-wrap: anywhere;
      }

      .metric {
        margin-bottom: 6px;
        font-size: 30px;
        line-height: 34px;
        font-weight: var(--font-weight-semibold, 700);
        letter-spacing: -0.02em;
      }

      .status-ok { color: var(--ops-success); }
      .status-fail { color: var(--ops-danger); }
      .status-unknown { color: var(--ops-muted); }

      .health-meta,
      .panel-note,
      .analysis-note {
        margin-top: 10px;
      }

      .panel-note,
      .analysis-note {
        margin-bottom: 0;
      }

      .actions {
        display: grid;
        grid-template-columns: repeat(2, minmax(0, 1fr));
        gap: 8px;
      }

      .actions button { width: 100%; }

      .logs-panel {
        grid-column: 1 / -1;
        overflow: hidden;
      }

      .logs-toolbar {
        display: flex;
        align-items: flex-start;
        justify-content: space-between;
        gap: 12px;
        padding: 14px;
        border-bottom: 1px solid var(--ops-border);
      }

      .logs-toolbar h2 { margin-bottom: 2px; }

      .logs-controls {
        display: flex;
        gap: 8px;
        flex-wrap: wrap;
        justify-content: flex-end;
        align-items: center;
      }

      .logs-controls select { min-width: 108px; }
      #logTail { width: 86px; }
      #logSearch { width: min(100%, 260px); }

      .checkbox-control {
        display: inline-flex;
        align-items: center;
        gap: 6px;
        min-height: 38px;
        border: 1px solid var(--ops-border);
        border-radius: var(--ops-radius-sm);
        padding: 0 9px;
        background: var(--ops-control);
        color: var(--ops-muted);
        white-space: nowrap;
      }

      .checkbox-control input { min-height: auto; }

      #logs {
        min-height: 330px;
        max-height: 560px;
        overflow: auto;
        background: var(--ops-log-bg);
        color: var(--ops-log-text);
        font-family: var(--font-mono, "SFMono-Regular", Consolas, "Liberation Mono", monospace);
        font-size: 12px;
        line-height: 18px;
        padding: 8px 0;
      }

      .log-line {
        display: grid;
        grid-template-columns: 58px minmax(0, 1fr);
        gap: 10px;
        padding: 1px 12px;
        white-space: pre-wrap;
      }

      .log-line:hover { background: rgba(255, 255, 255, 0.055); }
      .log-line.error { color: #ff7b72; }
      .log-line.warn { color: #d29922; }
      .log-line.info { color: #79c0ff; }

      .log-meta {
        color: #8b949e;
        text-align: right;
        user-select: none;
      }

      .empty {
        padding: 30px 14px;
        color: #8b949e;
        text-align: center;
      }

      .analysis-panel {
        margin: 12px;
        border: 1px solid var(--ops-border);
        border-radius: var(--ops-radius-md);
        padding: 12px;
        background: var(--ops-surface-muted);
      }

      .analysis-summary {
        display: flex;
        align-items: center;
        gap: 8px;
        flex-wrap: wrap;
        margin-bottom: 10px;
      }

      .notice {
        position: sticky;
        bottom: 12px;
        margin-top: 14px;
        border: 1px solid var(--ops-border);
        border-radius: var(--ops-radius-md);
        padding: 10px 12px;
        background: var(--ops-surface-raised);
        color: var(--ops-muted);
        box-shadow: var(--ops-shadow);
        transform-origin: bottom center;
        transition:
          opacity 160ms var(--ops-ease),
          transform 160ms var(--ops-ease);
      }

      .notice.error {
        color: var(--ops-danger);
        border-color: color-mix(in srgb, var(--ops-danger) 54%, var(--ops-border));
      }

      .notice.success {
        color: var(--ops-success);
        border-color: color-mix(in srgb, var(--ops-success) 54%, var(--ops-border));
      }

      .hidden { display: none; }

      @media (max-width: 860px) {
        main { padding: 14px; }
        .topbar, .logs-toolbar { display: grid; }
        .status-stack { justify-items: start; min-width: 0; }
        .input-row { grid-template-columns: 1fr; }
        .input-row button { width: 100%; }
        .summary-grid { grid-template-columns: 1fr; }
        .logs-controls { justify-content: flex-start; }
        #logSearch { width: 100%; }
      }

      @media (max-width: 520px) {
        .actions { grid-template-columns: 1fr; }
        .kv { grid-template-columns: 1fr; }
        .logs-controls > * { width: 100%; }
        .checkbox-control { justify-content: center; }
      }

      @media (prefers-reduced-motion: reduce) {
        *, *::before, *::after {
          animation-duration: 0.01ms !important;
          animation-iteration-count: 1 !important;
          scroll-behavior: auto !important;
          transition-duration: 0.01ms !important;
        }
      }
    </style>
  </head>
  <body>
    <main aria-busy="false">
      <header class="topbar">
        <div>
          <div class="title-row">
            <h1>${canvasTitle}</h1>
            <span id="providerBadge" class="badge warn">URL needed</span>
          </div>
          <p class="lede">Operate health, lifecycle, and recent logs for the deployed Azure Container App. Start with the public URL; the canvas handles Azure resource discovery.</p>
        </div>
        <div class="status-stack">
          <span id="lastUpdated" class="muted">Not loaded yet</span>
          <span class="muted">Canvas <code>${safeInstanceId}</code></span>
        </div>
      </header>

      <section class="connection-panel" aria-labelledby="connectTitle">
        <div class="connection-copy">
          <div>
            <h2 id="connectTitle">Connect to the deployed app</h2>
            <p class="muted">Use the app URL as the stable identity. Infrastructure details stay secondary until they are needed.</p>
          </div>
          <span class="badge" aria-label="Azure Container Apps discovery">az discovery</span>
        </div>
        <div class="input-row">
          <div class="field">
            <label for="baseUrl">Public URL</label>
            <input id="baseUrl" aria-label="Public app URL" autocomplete="off" spellcheck="false" placeholder="https://...azurecontainerapps.io" value="${initialUrl}" />
          </div>
          <button id="discover" class="button-primary">Discover</button>
          <button id="reload">Reload state</button>
        </div>
      </section>

      <section class="summary-grid" aria-label="Deployed app summary">
        <article class="panel" aria-labelledby="deploymentTitle">
          <div class="panel-header">
            <h2 id="deploymentTitle">Deployment</h2>
          </div>
          <div class="kv">
            <span class="key">App</span><span id="appName" class="value">-</span>
            <span class="key">Resource group</span><span id="resourceGroup" class="value">-</span>
            <span class="key">Subscription</span><span id="subscriptionId" class="value">-</span>
            <span class="key">Endpoint</span><span id="baseUrlValue" class="value">-</span>
          </div>
        </article>

        <article class="panel" aria-labelledby="healthTitle">
          <div class="panel-header">
            <h2 id="healthTitle">Health</h2>
          </div>
          <div id="healthStatus" class="metric status-unknown">Unknown</div>
          <div id="healthSummary" class="muted">No health check has run.</div>
          <div class="kv health-meta">
            <span class="key">Last checked</span><span id="checkedAt" class="value">-</span>
          </div>
        </article>

        <article class="panel" aria-labelledby="actionsTitle">
          <div class="panel-header">
            <h2 id="actionsTitle">Lifecycle</h2>
          </div>
          <div class="actions">
            <button id="refresh">Refresh health</button>
            <button id="start">Start</button>
            <button id="restart">Restart</button>
            <button id="stop" class="button-danger" aria-describedby="lifecycleHint">Stop</button>
          </div>
          <p id="lifecycleHint" class="muted panel-note">Lifecycle actions use Azure CLI after discovery identifies the Container App.</p>
        </article>

        <article class="logs-panel" aria-labelledby="logsTitle">
          <div class="logs-toolbar">
            <div>
              <h2 id="logsTitle">Logs</h2>
              <span id="logSummary" class="muted">No logs loaded.</span>
            </div>
            <div class="logs-controls">
              <select id="logType" aria-label="Log type">
                <option value="console">Console</option>
                <option value="system">System</option>
              </select>
              <input id="logTail" aria-label="Tail count" type="number" min="1" max="300" value="100" />
              <input id="logSearch" aria-label="Filter logs" placeholder="Filter logs..." />
              <label class="checkbox-control"><input id="errorsOnly" type="checkbox" /> errors only</label>
              <label class="checkbox-control"><input id="autoLogs" type="checkbox" /> auto-refresh</label>
              <button id="loadLogs">Load logs</button>
              <button id="analyzeFailures" class="button-primary">Analyze failures</button>
              <button id="copyLogs">Copy</button>
            </div>
          </div>
          <div id="logs"><div class="empty">Load console or system logs to inspect recent Azure Container App output.</div></div>
          <div id="analysis" class="analysis-panel hidden"></div>
        </article>
      </section>

      <div id="notice" class="notice hidden" aria-live="polite"></div>
    </main>

    <script>
      const state = { logs: [], analysis: null, autoTimer: null, busy: new Set() };
      const noticeEl = document.getElementById("notice");
      const baseUrlEl = document.getElementById("baseUrl");
      const logsEl = document.getElementById("logs");
      const mainEl = document.querySelector("main");

      function byId(id) { return document.getElementById(id); }

      function baseUrlValue() {
        return baseUrlEl.value.trim();
      }

      function requireBaseUrl() {
        const value = baseUrlValue();
        if (!value) throw new Error("Enter the deployed app URL first.");
        return value;
      }

      function setNotice(message, kind = "info") {
        noticeEl.textContent = message;
        noticeEl.className = "notice " + (kind === "error" ? "error" : kind === "success" ? "success" : "");
        noticeEl.setAttribute("role", kind === "error" ? "alert" : "status");
        noticeEl.classList.remove("hidden");
      }

      function setBusy(key, isBusy) {
        if (isBusy) state.busy.add(key); else state.busy.delete(key);
        const busy = state.busy.size > 0;
        mainEl.setAttribute("aria-busy", busy ? "true" : "false");
        for (const button of document.querySelectorAll("button")) {
          button.disabled = busy;
        }
      }

      function escapeText(value) {
        return String(value ?? "")
          .replaceAll("&", "&amp;")
          .replaceAll("<", "&lt;")
          .replaceAll(">", "&gt;")
          .replaceAll('"', "&quot;")
          .replaceAll("'", "&#39;");
      }

      function stripTags(value) {
        return String(value ?? "").replace(/<[^>]*>/g, " ").replace(/\\s+/g, " ").trim();
      }

      function short(value, max = 150) {
        const text = String(value ?? "-");
        return text.length > max ? text.slice(0, max - 1) + "..." : text;
      }

      function localTime(value) {
        if (!value) return "-";
        const date = new Date(value);
        return Number.isNaN(date.getTime()) ? value : date.toLocaleString();
      }

      function healthStatusFromPayload(payload) {
        if (payload == null) return { label: "Unknown", className: "status-unknown", summary: "No response body." };
        if (typeof payload === "string") return { label: "Reachable", className: "status-ok", summary: short(stripTags(payload)) };
        if (typeof payload === "object") {
          if (typeof payload.text === "string") {
            return { label: "Reachable", className: "status-ok", summary: short(stripTags(payload.text)) || "Endpoint returned non-JSON content." };
          }
          const candidate = payload.status ?? payload.state ?? payload.health ?? payload.overallStatus;
          const text = typeof candidate === "string" ? candidate.toLowerCase() : "";
          if (text.includes("ok") || text.includes("healthy") || text.includes("up") || text.includes("running")) {
            return { label: "Healthy", className: "status-ok", summary: short(JSON.stringify(payload)) };
          }
          if (text.includes("fail") || text.includes("down") || text.includes("unhealthy") || text.includes("error")) {
            return { label: "Unhealthy", className: "status-fail", summary: short(JSON.stringify(payload)) };
          }
          return { label: "Reachable", className: "status-ok", summary: short(JSON.stringify(payload)) };
        }
        return { label: "Unknown", className: "status-unknown", summary: String(payload) };
      }

      function resetInstanceView(message = "Enter a URL to load the deployed app.") {
        byId("providerBadge").textContent = "URL needed";
        byId("providerBadge").className = "badge warn";
        byId("appName").textContent = "-";
        byId("resourceGroup").textContent = "-";
        byId("subscriptionId").textContent = "-";
        byId("baseUrlValue").textContent = "-";
        byId("checkedAt").textContent = "-";
        byId("healthStatus").textContent = "Unknown";
        byId("healthStatus").className = "metric status-unknown";
        byId("healthSummary").textContent = message;
        byId("lastUpdated").textContent = "Not loaded yet";
      }

      function renderInstance(result) {
        const config = result.config ?? {};
        const mgmt = config.management ?? {};
        byId("appName").textContent = mgmt.appName ?? "(not discovered)";
        byId("resourceGroup").textContent = mgmt.resourceGroup ?? "-";
        byId("subscriptionId").textContent = mgmt.subscriptionId ?? "-";
        byId("baseUrlValue").textContent = config.baseUrl ?? "-";
        byId("providerBadge").textContent = mgmt.kind === "azure-container-app" ? "Azure Container Apps" : "HTTP endpoint";
        byId("providerBadge").className = "badge " + (mgmt.kind === "azure-container-app" ? "ok" : "warn");
        if (config.baseUrl) baseUrlEl.value = config.baseUrl;

        const status = result.status ?? null;
        byId("checkedAt").textContent = localTime(status?.checkedAt);
        const health = healthStatusFromPayload(status?.response ?? null);
        const statusEl = byId("healthStatus");
        statusEl.textContent = health.label;
        statusEl.className = "metric " + health.className;
        byId("healthSummary").textContent = health.summary;
        byId("lastUpdated").textContent = "Loaded " + new Date().toLocaleTimeString();
      }

      async function requestApi(path, method = "GET") {
        const baseUrl = requireBaseUrl();
        const url = new URL(path, window.location.href);
        url.searchParams.set("url", baseUrl);
        const response = await fetch(url, { method });
        const data = await response.json();
        if (!response.ok) throw new Error(data.error ?? "Request failed");
        return data;
      }

      async function loadInstance() {
        if (!baseUrlValue()) {
          resetInstanceView();
          return;
        }
        const data = await requestApi("/api/instance");
        renderInstance(data);
      }

      async function discover() {
        const baseUrl = requireBaseUrl();
        const url = new URL("/api/discover", window.location.href);
        url.searchParams.set("url", baseUrl);
        const response = await fetch(url, { method: "POST" });
        const data = await response.json();
        if (!response.ok) throw new Error(data.error ?? "Discovery failed");
        renderInstance({ config: data.config, status: null });
        setNotice("Discovered " + data.config.management.appName + " in " + data.config.management.resourceGroup + ".", "success");
      }

      async function refreshHealth() {
        const data = await requestApi("/api/refresh", "POST");
        const status = data.status ?? null;
        byId("checkedAt").textContent = localTime(status?.checkedAt);
        const health = healthStatusFromPayload(status?.response ?? null);
        const statusEl = byId("healthStatus");
        statusEl.textContent = health.label;
        statusEl.className = "metric " + health.className;
        byId("healthSummary").textContent = health.summary;
        byId("lastUpdated").textContent = "Health refreshed " + new Date().toLocaleTimeString();
        setNotice("Health refreshed.", "success");
      }

      async function lifecycle(action) {
        const data = await requestApi("/api/" + action, "POST");
        setNotice(action[0].toUpperCase() + action.slice(1) + " completed via " + data.mode + ".", "success");
        if (action !== "stop") await refreshHealth();
      }

      function classifyLog(line) {
        const lower = line.toLowerCase();
        if (lower.includes(" fail") || lower.includes(" error") || lower.includes("exception") || lower.includes("critical")) return "error";
        if (lower.includes(" warn") || lower.includes("warning")) return "warn";
        if (lower.includes(" info") || lower.includes("stdout")) return "info";
        return "";
      }

      function renderLogs() {
        const query = byId("logSearch").value.trim().toLowerCase();
        const errorsOnly = byId("errorsOnly").checked;
        const filtered = state.logs.filter((line) => {
          const classification = classifyLog(line);
          if (errorsOnly && classification !== "error") return false;
          if (query && !line.toLowerCase().includes(query)) return false;
          return true;
        });
        byId("logSummary").textContent = filtered.length + " of " + state.logs.length + " line(s) shown";
        if (filtered.length === 0) {
          logsEl.innerHTML = '<div class="empty">' + (state.logs.length === 0 ? "No logs loaded yet." : "No log lines match the current filters.") + '</div>';
          return;
        }
        logsEl.innerHTML = filtered.map((line, index) => {
          const cls = classifyLog(line);
          return '<div class="log-line ' + cls + '"><span class="log-meta">' + (index + 1) + '</span><span>' + escapeText(line) + '</span></div>';
        }).join("");
      }

      async function loadLogs() {
        const type = byId("logType").value;
        const tail = byId("logTail").value || "100";
        const url = new URL("/api/logs", window.location.href);
        url.searchParams.set("url", requireBaseUrl());
        url.searchParams.set("type", type);
        url.searchParams.set("tail", tail);
        const response = await fetch(url);
        const data = await response.json();
        if (!response.ok) throw new Error(data.error ?? "Failed to load logs");
        state.logs = Array.isArray(data.lines) ? data.lines : [];
        renderLogs();
        logsEl.scrollTop = logsEl.scrollHeight;
        byId("lastUpdated").textContent = "Logs loaded " + new Date().toLocaleTimeString();
        setNotice("Loaded " + data.lineCount + " " + type + " log line(s).", "success");
      }

      function renderAnalysisRequest(result) {
        state.analysis = result;
        const panel = byId("analysis");
        panel.classList.remove("hidden");
        if (!result) {
          panel.innerHTML = '<div class="empty">No analysis request has been sent.</div>';
          return;
        }
        panel.innerHTML =
          '<div class="analysis-summary">' +
            '<span class="badge ok">sent to agent</span>' +
            '<strong>' + escapeText(result.summary ?? "Logs were sent to the chat for agent analysis.") + '</strong>' +
            '<span class="muted">Sent at ' + escapeText(localTime(result.sentAt)) + '</span>' +
          '</div>' +
          '<div class="kv">' +
            '<span class="key">Message ID</span><span class="value">' + escapeText(result.messageId ?? "-") + '</span>' +
            '<span class="key">Log lines</span><span class="value">' + escapeText(result.logs?.lineCount ?? 0) + '</span>' +
          '</div>' +
          '<p class="muted analysis-note">The agent will analyze the full log context in chat and respond there.</p>';
      }

      async function analyzeFailures() {
        const type = byId("logType").value;
        const tail = byId("logTail").value || "200";
        const url = new URL("/api/analyze", window.location.href);
        url.searchParams.set("url", requireBaseUrl());
        url.searchParams.set("type", type);
        url.searchParams.set("tail", tail);
        const response = await fetch(url, { method: "POST" });
        const data = await response.json();
        if (!response.ok) throw new Error(data.error ?? "Failed to analyze failures");
        state.logs = Array.isArray(data.logs?.lines) ? data.logs.lines : [];
        renderLogs();
        renderAnalysisRequest(data);
        logsEl.scrollTop = logsEl.scrollHeight;
        byId("lastUpdated").textContent = "Sent for analysis " + new Date().toLocaleTimeString();
        setNotice(data.summary ?? "Sent logs to the agent for analysis.", "success");
      }

      async function copyLogs() {
        const text = state.logs.join("\\n");
        if (!text) throw new Error("No logs to copy.");
        await navigator.clipboard.writeText(text);
        setNotice("Copied logs to clipboard.", "success");
      }

      function updateAutoLogs() {
        if (state.autoTimer) {
          clearInterval(state.autoTimer);
          state.autoTimer = null;
        }
        if (byId("autoLogs").checked) {
          state.autoTimer = setInterval(() => runSafely("logs", loadLogs), 15000);
          setNotice("Auto-refreshing logs every 15 seconds.");
        }
      }

      async function runSafely(key, work) {
        try {
          setBusy(key, true);
          await work();
        } catch (error) {
          setNotice(error.message ?? String(error), "error");
        } finally {
          setBusy(key, false);
        }
      }

      byId("discover").addEventListener("click", () => runSafely("discover", discover));
      byId("reload").addEventListener("click", () => runSafely("load", loadInstance));
      byId("refresh").addEventListener("click", () => runSafely("health", refreshHealth));
      byId("start").addEventListener("click", () => runSafely("start", () => lifecycle("start")));
      byId("stop").addEventListener("click", () => runSafely("stop", () => lifecycle("stop")));
      byId("restart").addEventListener("click", () => runSafely("restart", () => lifecycle("restart")));
      byId("loadLogs").addEventListener("click", () => runSafely("logs", loadLogs));
      byId("analyzeFailures").addEventListener("click", () => runSafely("analysis", analyzeFailures));
      byId("copyLogs").addEventListener("click", () => runSafely("copy", copyLogs));
      byId("logSearch").addEventListener("input", renderLogs);
      byId("errorsOnly").addEventListener("change", renderLogs);
      byId("autoLogs").addEventListener("change", updateAutoLogs);
      runSafely("load", loadInstance);
    </script>
  </body>
</html>`;
}
