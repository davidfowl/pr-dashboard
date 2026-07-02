import { createServer } from "node:http";
import { execFile } from "node:child_process";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import { homedir } from "node:os";
import { join } from "node:path";
import { promisify } from "node:util";
import { CanvasError, createCanvas, joinSession } from "@github/copilot-sdk/extension";
import { renderHtml } from "./renderer.mjs";

const servers = new Map();
const execFileAsync = promisify(execFile);
const COPILOT_HOME = process.env.COPILOT_HOME ?? join(homedir(), ".copilot");
const ARTIFACTS_DIR = join(COPILOT_HOME, "extensions", "deployed-instance-canvas", "artifacts");
const STATE_PATH = join(ARTIFACTS_DIR, "instances.json");
let copilotSession;

const openInputSchema = {
    type: "object",
    additionalProperties: false,
    properties: {
        deploymentId: { type: "string", minLength: 1 },
        url: { type: "string", minLength: 1 },
        title: { type: "string", minLength: 1 },
    },
};

const configInputSchema = {
    type: "object",
    additionalProperties: false,
    required: ["deploymentId", "baseUrl"],
    properties: {
        deploymentId: { type: "string", minLength: 1 },
        baseUrl: { type: "string", minLength: 1 },
        statusPath: { type: "string", minLength: 1 },
        startPath: { type: "string", minLength: 1 },
        stopPath: { type: "string", minLength: 1 },
        restartPath: { type: "string", minLength: 1 },
        headers: {
            type: "object",
            additionalProperties: { type: "string" },
            default: {},
        },
    },
};

const deploymentIdInputSchema = {
    type: "object",
    additionalProperties: false,
    properties: {
        deploymentId: { type: "string", minLength: 1 },
        url: { type: "string", minLength: 1 },
    },
};

const timeoutInputSchema = {
    type: "object",
    additionalProperties: false,
    properties: {
        deploymentId: { type: "string", minLength: 1 },
        url: { type: "string", minLength: 1 },
        timeoutMs: { type: "integer", minimum: 1, maximum: 120000 },
    },
};

const discoverInputSchema = {
    type: "object",
    additionalProperties: false,
    required: ["url"],
    properties: {
        deploymentId: { type: "string", minLength: 1 },
        url: { type: "string", minLength: 1 },
        statusPath: { type: "string", minLength: 1 },
        headers: {
            type: "object",
            additionalProperties: { type: "string" },
            default: {},
        },
    },
};

const logsInputSchema = {
    type: "object",
    additionalProperties: false,
    properties: {
        deploymentId: { type: "string", minLength: 1 },
        url: { type: "string", minLength: 1 },
        tail: { type: "integer", minimum: 1, maximum: 300 },
        type: { type: "string", enum: ["console", "system"] },
    },
};

const analyzeFailuresInputSchema = logsInputSchema;

function normalizeConfig(input) {
    const trim = (value, fallback) => {
        if (typeof value === "string" && value.trim().length > 0) {
            return value.trim();
        }
        return fallback;
    };
    return {
        deploymentId: input.deploymentId.trim(),
        baseUrl: input.baseUrl.trim().replace(/\/+$/, ""),
        statusPath: trim(input.statusPath, "/health"),
        startPath: trim(input.startPath, "/start"),
        stopPath: trim(input.stopPath, "/stop"),
        restartPath: trim(input.restartPath, "/restart"),
        headers: input.headers ?? {},
    };
}

function parseJsonMaybe(text) {
    if (!text || text.trim().length === 0) {
        return null;
    }
    try {
        return JSON.parse(text);
    } catch {
        return { text };
    }
}

async function runAz(args) {
    try {
        const { stdout } = await execFileAsync("az", args, { maxBuffer: 1024 * 1024 * 4 });
        return stdout.trim();
    } catch (error) {
        if (error?.code === "ENOENT") {
            throw new CanvasError("az_not_found", "Azure CLI (az) is not installed or not on PATH.");
        }
        const stderr = typeof error?.stderr === "string" ? error.stderr.trim() : "";
        const stdout = typeof error?.stdout === "string" ? error.stdout.trim() : "";
        throw new CanvasError(
            "az_command_failed",
            `Azure CLI command failed: az ${args.join(" ")}${stderr ? `\n${stderr}` : stdout ? `\n${stdout}` : ""}`
        );
    }
}

function getSubscriptionIdFromResourceId(resourceId) {
    const match = /^\/subscriptions\/([^/]+)\//i.exec(resourceId ?? "");
    return match?.[1] ?? null;
}

function getSubscriptionArgs(instance) {
    if (instance?.management?.subscriptionId) {
        return ["--subscription", instance.management.subscriptionId];
    }
    return [];
}

function normalizeDeploymentUrl(rawUrl) {
    const trimmed = String(rawUrl ?? "").trim();
    if (trimmed.length === 0) {
        throw new CanvasError("url_required", "URL is required.");
    }
    const value = /^[a-z][a-z0-9+.-]*:\/\//i.test(trimmed) ? trimmed : `https://${trimmed}`;
    try {
        const parsed = new URL(value);
        if (!parsed.hostname) {
            throw new CanvasError("invalid_url", "URL hostname is required.");
        }
        return parsed;
    } catch {
        throw new CanvasError("invalid_url", `Invalid URL: "${rawUrl}"`);
    }
}

function deploymentIdFromUrl(rawUrl) {
    return normalizeDeploymentUrl(rawUrl).hostname.toLowerCase();
}

function deploymentIdFromInput(input) {
    if (typeof input?.url === "string" && input.url.trim().length > 0) {
        return deploymentIdFromUrl(input.url);
    }
    if (typeof input?.deploymentId === "string" && input.deploymentId.trim().length > 0) {
        return input.deploymentId.trim();
    }
    throw new CanvasError("deployment_reference_required", "Provide the deployed app URL.");
}

async function discoverAzureContainerAppByUrl(rawUrl) {
    const parsed = normalizeDeploymentUrl(rawUrl);
    const hostname = parsed.hostname.toLowerCase();
    const query = `[?properties.configuration.ingress.fqdn=='${hostname}'].{name:name,resourceGroup:resourceGroup,id:id}`;
    const output = await runAz(["containerapp", "list", "--query", query, "-o", "json"]);
    const results = parseJsonMaybe(output);
    if (!Array.isArray(results) || results.length === 0) {
        throw new CanvasError("azure_not_found", `No Azure Container App found for FQDN "${hostname}".`);
    }
    const match = results[0];
    return {
        hostname,
        baseUrl: parsed.origin,
        appName: match.name,
        resourceGroup: match.resourceGroup,
        containerAppId: match.id,
        subscriptionId: getSubscriptionIdFromResourceId(match.id),
    };
}

async function runLifecycleAction(deploymentId, action) {
    const { instance } = await requireInstance(deploymentId);
    if (instance?.management?.kind === "azure-container-app") {
        const args = [
            "containerapp",
            action,
            "-n",
            instance.management.appName,
            "-g",
            instance.management.resourceGroup,
            ...getSubscriptionArgs(instance),
            "-o",
            "json",
        ];
        const output = await runAz(args);
        return {
            mode: "azure-container-app",
            command: `az ${args.join(" ")}`,
            response: parseJsonMaybe(output),
        };
    }

    const actionPath = action === "start" ? instance.startPath : action === "stop" ? instance.stopPath : instance.restartPath;
    const urlToCall = toUrl(instance.baseUrl, actionPath);
    const response = await requestJson(urlToCall, { method: "POST", headers: instance.headers }, 30000);
    return { mode: "http-endpoint", response };
}

async function getAzureLogsForDeployment(deploymentId, options = {}) {
    const { instance } = await requireInstance(deploymentId);
    if (instance?.management?.kind !== "azure-container-app") {
        throw new CanvasError("logs_not_supported", "Logs require Azure Container App discovery for this deployment.");
    }

    const tail = options.tail ?? 100;
    const type = options.type === "system" ? "system" : "console";
    const args = [
        "containerapp",
        "logs",
        "show",
        "-n",
        instance.management.appName,
        "-g",
        instance.management.resourceGroup,
        "--tail",
        String(tail),
        "--type",
        type,
        "--format",
        "text",
        "--only-show-errors",
        ...getSubscriptionArgs(instance),
    ];
    const output = await runAz(args);
    const lines = output.length === 0 ? [] : output.split(/\r?\n/);
    return {
        mode: "azure-container-app",
        command: `az ${args.join(" ")}`,
        lineCount: lines.length,
        lines,
    };
}

function buildFailureAnalysisPrompt(deploymentId, instance, logs, options) {
    const management = instance.management ?? {};
    const logText = logs.lines.join("\n");
    return `Analyze failures in the deployed Azure Container App logs.

Context:
- Deployment ID: ${deploymentId}
- Azure resource: ${management.appName ?? "(unknown)"}
- Resource group: ${management.resourceGroup ?? "(unknown)"}
- Subscription: ${management.subscriptionId ?? "(unknown)"}
- Public URL: ${instance.baseUrl}
- Log type: ${options.type === "system" ? "system" : "console"}
- Tail: ${options.tail ?? 200}
- Command used: ${logs.command}

Please analyze the logs like an engineer, not with simple keyword matching. Identify the root cause, impacted request or component if visible, why it is happening, and the next concrete fixes or Azure/GitHub checks to run. Keep the answer concise and actionable.

Logs:
\`\`\`
${logText}
\`\`\``;
}

async function sendFailureAnalysisToAgent(deploymentId, options = {}) {
    if (!copilotSession) {
        throw new CanvasError("session_unavailable", "Copilot session is not ready to receive analysis requests.");
    }

    const { instance } = await requireInstance(deploymentId);
    const logs = await getAzureLogsForDeployment(deploymentId, options);
    const prompt = buildFailureAnalysisPrompt(deploymentId, instance, logs, options);
    const messageId = await copilotSession.send({ prompt });
    return {
        logs,
        sentAt: new Date().toISOString(),
        messageId,
        status: "sent-to-agent",
        summary: `Sent ${logs.lineCount} ${options.type === "system" ? "system" : "console"} log line(s) to the agent for analysis.`,
    };
}

async function ensureArtifactsDir() {
    await mkdir(ARTIFACTS_DIR, { recursive: true });
}

async function readState() {
    await ensureArtifactsDir();
    try {
        const raw = await readFile(STATE_PATH, "utf8");
        const parsed = JSON.parse(raw);
        if (!parsed || typeof parsed !== "object" || !parsed.instances || typeof parsed.instances !== "object") {
            throw new Error("State payload is malformed.");
        }
        return parsed;
    } catch (error) {
        if (error?.code === "ENOENT") {
            return { instances: {}, statuses: {} };
        }
        throw new CanvasError("state_read_failed", `Failed to read persisted state: ${String(error.message ?? error)}`);
    }
}

async function writeState(state) {
    await ensureArtifactsDir();
    try {
        await writeFile(STATE_PATH, `${JSON.stringify(state, null, 2)}\n`, "utf8");
    } catch (error) {
        throw new CanvasError("state_write_failed", `Failed to persist state: ${String(error.message ?? error)}`);
    }
}

function toUrl(baseUrl, path) {
    const safePath = path.startsWith("/") ? path : `/${path}`;
    return `${baseUrl}${safePath}`;
}

async function requestJson(url, options, timeoutMs) {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), timeoutMs);
    try {
        const response = await fetch(url, { ...options, signal: controller.signal });
        const text = await response.text();
        let payload = null;
        if (text.length > 0) {
            try {
                payload = JSON.parse(text);
            } catch {
                payload = { text };
            }
        }
        if (!response.ok) {
            throw new CanvasError(
                "remote_request_failed",
                `Request to ${url} failed with ${response.status}: ${typeof payload === "string" ? payload : JSON.stringify(payload)}`
            );
        }
        return payload;
    } catch (error) {
        if (error instanceof CanvasError) {
            throw error;
        }
        throw new CanvasError("remote_request_error", `Request to ${url} failed: ${String(error.message ?? error)}`);
    } finally {
        clearTimeout(timeout);
    }
}

async function requireInstance(deploymentId) {
    const state = await readState();
    const instance = state.instances[deploymentId];
    if (instance) {
        return { state, instance, deploymentId };
    }
    const requestedHost = String(deploymentId ?? "").toLowerCase();
    const matchedEntry = Object.entries(state.instances).find(([, candidate]) => {
        try {
            return new URL(candidate.baseUrl).hostname.toLowerCase() === requestedHost;
        } catch {
            return false;
        }
    });
    if (matchedEntry) {
        const [storedDeploymentId, matchedInstance] = matchedEntry;
        return { state, instance: matchedInstance, deploymentId: storedDeploymentId };
    }
    throw new CanvasError("instance_not_found", `No deployment config exists for "${deploymentId}".`);
}

async function refreshStatusForDeployment(deploymentId, timeoutMs = 10000) {
    const resolved = await requireInstance(deploymentId);
    const { state, instance } = resolved;
    const url = toUrl(instance.baseUrl, instance.statusPath);
    const payload = await requestJson(url, { method: "GET", headers: instance.headers }, timeoutMs);
    const status = {
        checkedAt: new Date().toISOString(),
        response: payload,
    };
    state.statuses[resolved.deploymentId] = status;
    await writeState(state);
    return status;
}

function renderLegacyHtml(instanceId, openInput) {
    const deploymentId = openInput?.deploymentId ?? "";
    return `<!doctype html>
<html>
  <head>
    <meta charset="utf-8" />
    <title>Deployed Instance Manager</title>
    <style>
      body {
        margin: 0;
        background: var(--background-color-default, #ffffff);
        color: var(--text-color-default, #1f2328);
        font-family: var(--font-sans, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif);
        font-size: var(--text-body-medium, 14px);
        line-height: var(--leading-body-medium, 20px);
      }
      main { padding: 16px; max-width: 1024px; }
      .muted { color: var(--text-color-muted, #656d76); }
      code {
        font-family: var(--font-mono, "SFMono-Regular", Consolas, "Liberation Mono", monospace);
        font-size: var(--text-code-inline, 12px);
      }
      input, button, select {
        font: inherit;
        border: 1px solid var(--border-color-default, #d0d7de);
        border-radius: 6px;
        padding: 8px;
        background: var(--background-color-default, #ffffff);
        color: var(--text-color-default, #1f2328);
      }
      button { cursor: pointer; }
      .row { display: flex; gap: 8px; flex-wrap: wrap; margin-bottom: 12px; align-items: center; }
      .grid { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
      .full-width { grid-column: 1 / -1; }
      .card {
        border: 1px solid var(--border-color-default, #d0d7de);
        border-radius: 6px;
        padding: 12px;
      }
      .label { font-weight: 600; min-width: 150px; display: inline-block; }
      .status-ok { color: #1a7f37; font-weight: 600; }
      .status-fail { color: #d1242f; font-weight: 600; }
      .status-unknown { color: var(--text-color-muted, #656d76); font-weight: 600; }
      #logs {
        border: 1px solid var(--border-color-default, #d0d7de);
        border-radius: 6px;
        padding: 12px;
        min-height: 220px;
        max-height: 500px;
        overflow: auto;
        background: #0d1117;
        color: #c9d1d9;
        font-family: var(--font-mono, "SFMono-Regular", Consolas, "Liberation Mono", monospace);
        font-size: 12px;
        white-space: pre-wrap;
      }
    </style>
  </head>
  <body>
    <main>
      <h1>Deployed Instance Manager</h1>
      <p class="muted">Canvas instance: <code>${instanceId}</code></p>
      <div class="row">
        <input id="deploymentId" placeholder="deploymentId" value="${deploymentId}" />
        <input id="baseUrl" placeholder="https://...azurecontainerapps.io" style="min-width: 340px;" />
        <button id="discover">Discover Azure app</button>
      </div>
      <div class="grid">
        <section class="card">
          <h3>Deployment</h3>
          <div><span class="label">Name</span><span id="appName">-</span></div>
          <div><span class="label">Resource group</span><span id="resourceGroup">-</span></div>
          <div><span class="label">Subscription</span><span id="subscriptionId">-</span></div>
          <div><span class="label">Base URL</span><span id="baseUrlValue">-</span></div>
        </section>
        <section class="card">
          <h3>Health</h3>
          <div><span class="label">Status</span><span id="healthStatus" class="status-unknown">Unknown</span></div>
          <div><span class="label">Last checked</span><span id="checkedAt">-</span></div>
          <div><span class="label">Summary</span><span id="healthSummary">-</span></div>
          <div class="row" style="margin-top:10px;">
            <button id="refresh">Refresh health</button>
            <button id="start">Start</button>
            <button id="stop">Stop</button>
            <button id="restart">Restart</button>
          </div>
        </section>
        <section class="card full-width">
          <h3>Logs</h3>
          <div class="row">
            <label>Type
              <select id="logType">
                <option value="console">Console</option>
                <option value="system">System</option>
              </select>
            </label>
            <label>Tail
              <input id="logTail" type="number" min="1" max="300" value="100" style="width:90px;" />
            </label>
            <button id="loadLogs">Load logs</button>
          </div>
          <div id="logs">No logs loaded yet.</div>
        </section>
      </div>
      <p id="notice" class="muted" aria-live="polite"></p>
    </main>
    <script>
      const noticeEl = document.getElementById("notice");
      const deploymentIdEl = document.getElementById("deploymentId");
      const baseUrlEl = document.getElementById("baseUrl");
      const logsEl = document.getElementById("logs");

      function setNotice(message, isError = false) {
        noticeEl.textContent = message;
        noticeEl.style.color = isError ? "var(--true-color-red, #d1242f)" : "var(--text-color-muted, #656d76)";
      }

      function healthStatusFromPayload(payload) {
        if (payload == null) return { label: "Unknown", className: "status-unknown", summary: "No response body." };
        if (typeof payload === "string") return { label: "OK", className: "status-ok", summary: payload };
        if (typeof payload === "object") {
          const candidate = payload.status ?? payload.state ?? payload.health ?? payload.overallStatus;
          const text = typeof candidate === "string" ? candidate.toLowerCase() : "";
          if (text.includes("ok") || text.includes("healthy") || text.includes("up") || text.includes("running")) {
            return { label: "Healthy", className: "status-ok", summary: JSON.stringify(payload).slice(0, 140) };
          }
          if (text.includes("fail") || text.includes("down") || text.includes("unhealthy") || text.includes("error")) {
            return { label: "Unhealthy", className: "status-fail", summary: JSON.stringify(payload).slice(0, 140) };
          }
          return { label: "Unknown", className: "status-unknown", summary: JSON.stringify(payload).slice(0, 140) };
        }
        return { label: "Unknown", className: "status-unknown", summary: String(payload) };
      }

      function renderInstance(result) {
        const config = result.config ?? {};
        const mgmt = config.management ?? {};
        document.getElementById("appName").textContent = mgmt.appName ?? "(not discovered)";
        document.getElementById("resourceGroup").textContent = mgmt.resourceGroup ?? "-";
        document.getElementById("subscriptionId").textContent = mgmt.subscriptionId ?? "-";
        document.getElementById("baseUrlValue").textContent = config.baseUrl ?? "-";
        if (config.baseUrl) {
          baseUrlEl.value = config.baseUrl;
        }
        const status = result.status ?? null;
        document.getElementById("checkedAt").textContent = status?.checkedAt ?? "-";
        const health = healthStatusFromPayload(status?.response ?? null);
        const statusEl = document.getElementById("healthStatus");
        statusEl.textContent = health.label;
        statusEl.className = health.className;
        document.getElementById("healthSummary").textContent = health.summary;
      }

      async function requestApi(path, method = "GET") {
        const deploymentId = deploymentIdEl.value.trim();
        const url = new URL(path, window.location.href);
        if (deploymentId.length > 0) {
          url.searchParams.set("deploymentId", deploymentId);
        }
        const response = await fetch(url, { method });
        const data = await response.json();
        if (!response.ok) {
          throw new Error(data.error ?? "Request failed");
        }
        return data;
      }

      async function loadInstance() {
        const data = await requestApi("/api/instance");
        renderInstance(data);
      }

      async function discover() {
        const deploymentId = deploymentIdEl.value.trim();
        const baseUrl = baseUrlEl.value.trim();
        const url = new URL("/api/discover", window.location.href);
        if (deploymentId.length > 0) {
          url.searchParams.set("deploymentId", deploymentId);
        }
        if (baseUrl.length > 0) {
          url.searchParams.set("url", baseUrl);
        }
        const response = await fetch(url, { method: "POST" });
        const data = await response.json();
        if (!response.ok) {
          throw new Error(data.error ?? "Discovery failed");
        }
        setNotice("Azure app discovered successfully.");
        await loadInstance();
      }

      async function refreshHealth() {
        const data = await requestApi("/api/refresh", "POST");
        const status = data.status ?? null;
        document.getElementById("checkedAt").textContent = status?.checkedAt ?? "-";
        const health = healthStatusFromPayload(status?.response ?? null);
        const statusEl = document.getElementById("healthStatus");
        statusEl.textContent = health.label;
        statusEl.className = health.className;
        document.getElementById("healthSummary").textContent = health.summary;
        setNotice("Health refreshed.");
      }

      async function lifecycle(action) {
        const data = await requestApi("/api/" + action, "POST");
        setNotice(action + " completed via " + data.mode + ".");
      }

      async function loadLogs() {
        const type = document.getElementById("logType").value;
        const tail = document.getElementById("logTail").value || "100";
        const deploymentId = deploymentIdEl.value.trim();
        const url = new URL("/api/logs", window.location.href);
        if (deploymentId.length > 0) {
          url.searchParams.set("deploymentId", deploymentId);
        }
        url.searchParams.set("type", type);
        url.searchParams.set("tail", tail);
        const response = await fetch(url);
        const data = await response.json();
        if (!response.ok) {
          throw new Error(data.error ?? "Failed to load logs");
        }
        if (!Array.isArray(data.lines) || data.lines.length === 0) {
          logsEl.textContent = "No logs returned.";
          return;
        }
        logsEl.textContent = data.lines.join("\\n");
        logsEl.scrollTop = logsEl.scrollHeight;
        setNotice("Loaded " + data.lineCount + " log line(s).");
      }

      async function runSafely(work) {
        try {
          await work();
        } catch (error) {
          setNotice(error.message ?? String(error), true);
        }
      }

      document.getElementById("discover").addEventListener("click", () => runSafely(discover));
      document.getElementById("refresh").addEventListener("click", () => runSafely(refreshHealth));
      document.getElementById("start").addEventListener("click", () => runSafely(() => lifecycle("start")));
      document.getElementById("stop").addEventListener("click", () => runSafely(() => lifecycle("stop")));
      document.getElementById("restart").addEventListener("click", () => runSafely(() => lifecycle("restart")));
      document.getElementById("loadLogs").addEventListener("click", () => runSafely(loadLogs));
      runSafely(loadInstance);
    </script>
  </body>
</html>`;
}

function parseDeploymentId(url, fallbackId) {
    const rawUrl = url.searchParams.get("url");
    if (rawUrl && rawUrl.trim().length > 0) {
        return deploymentIdFromUrl(rawUrl);
    }
    const deploymentId = url.searchParams.get("deploymentId");
    if (deploymentId && deploymentId.trim().length > 0) {
        return deploymentId.trim();
    }
    if (fallbackId && fallbackId.trim().length > 0) {
        return fallbackId.trim();
    }
    const baseUrl = url.searchParams.get("baseUrl");
    if (baseUrl && baseUrl.trim().length > 0) {
        return deploymentIdFromUrl(baseUrl);
    }
    return fallbackId;
}

async function sendJson(res, statusCode, payload) {
    res.statusCode = statusCode;
    res.setHeader("Content-Type", "application/json; charset=utf-8");
    res.end(`${JSON.stringify(payload, null, 2)}\n`);
}

async function handleApiRequest(req, res, openInput) {
    const url = new URL(req.url ?? "/", "http://127.0.0.1");
    const fallbackId = openInput?.url ? deploymentIdFromUrl(openInput.url) : openInput?.deploymentId;
    const deploymentId = parseDeploymentId(url, fallbackId);
    if (!deploymentId && url.pathname !== "/api/discover") {
        await sendJson(res, 400, { error: "url is required." });
        return;
    }

    if (url.pathname === "/api/discover" && req.method === "POST") {
        const rawUrl = url.searchParams.get("url");
        if (!rawUrl) {
            await sendJson(res, 400, { error: "url is required." });
            return;
        }
        const requestedDeploymentId = deploymentIdFromUrl(rawUrl);
        const discovery = await discoverAzureContainerAppByUrl(rawUrl);
        const state = await readState();
        const config = {
            deploymentId: requestedDeploymentId,
            baseUrl: discovery.baseUrl,
            statusPath: "/",
            startPath: "/start",
            stopPath: "/stop",
            restartPath: "/restart",
            headers: {},
            management: {
                kind: "azure-container-app",
                appName: discovery.appName,
                resourceGroup: discovery.resourceGroup,
                subscriptionId: discovery.subscriptionId,
                containerAppId: discovery.containerAppId,
                discoveredFromUrl: rawUrl,
            },
        };
        state.instances[requestedDeploymentId] = config;
        await writeState(state);
        await sendJson(res, 200, { deploymentId: requestedDeploymentId, config });
        return;
    }

    if (url.pathname === "/api/instance" && req.method === "GET") {
        const { state, instance, deploymentId: resolvedDeploymentId } = await requireInstance(deploymentId);
        await sendJson(res, 200, {
            deploymentId: resolvedDeploymentId,
            config: instance,
            status: state.statuses[resolvedDeploymentId] ?? null,
        });
        return;
    }

    if (url.pathname === "/api/refresh" && req.method === "POST") {
        const status = await refreshStatusForDeployment(deploymentId);
        await sendJson(res, 200, { deploymentId, status });
        return;
    }

    if (url.pathname === "/api/start" && req.method === "POST") {
        const result = await runLifecycleAction(deploymentId, "start");
        await sendJson(res, 200, { deploymentId, action: "start", ...result });
        return;
    }

    if (url.pathname === "/api/stop" && req.method === "POST") {
        const result = await runLifecycleAction(deploymentId, "stop");
        await sendJson(res, 200, { deploymentId, action: "stop", ...result });
        return;
    }

    if (url.pathname === "/api/restart" && req.method === "POST") {
        const result = await runLifecycleAction(deploymentId, "restart");
        await sendJson(res, 200, { deploymentId, action: "restart", ...result });
        return;
    }

    if (url.pathname === "/api/logs" && req.method === "GET") {
        const tailValue = Number.parseInt(url.searchParams.get("tail") ?? "100", 10);
        const tail = Number.isFinite(tailValue) && tailValue > 0 ? Math.min(tailValue, 300) : 100;
        const typeParam = url.searchParams.get("type");
        const type = typeParam === "system" ? "system" : "console";
        const logs = await getAzureLogsForDeployment(deploymentId, { tail, type });
        await sendJson(res, 200, { deploymentId, ...logs });
        return;
    }

    if (url.pathname === "/api/analyze" && req.method === "POST") {
        const tailValue = Number.parseInt(url.searchParams.get("tail") ?? "200", 10);
        const tail = Number.isFinite(tailValue) && tailValue > 0 ? Math.min(tailValue, 300) : 200;
        const typeParam = url.searchParams.get("type");
        const type = typeParam === "system" ? "system" : "console";
        const result = await sendFailureAnalysisToAgent(deploymentId, { tail, type });
        await sendJson(res, 200, { deploymentId, ...result });
        return;
    }

    await sendJson(res, 404, { error: "Not found." });
}

async function startServer(instanceId, openInput) {
    const serverState = { openInput: openInput ?? {} };
    const server = createServer(async (req, res) => {
        try {
            const currentOpenInput = serverState.openInput;
            const url = new URL(req.url ?? "/", "http://127.0.0.1");
            if (url.pathname.startsWith("/api/")) {
                await handleApiRequest(req, res, currentOpenInput);
                return;
            }
            res.setHeader("Content-Type", "text/html; charset=utf-8");
            res.end(renderHtml(instanceId, currentOpenInput));
        } catch (error) {
            await sendJson(res, 500, { error: String(error.message ?? error) });
        }
    });
    await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
    const address = server.address();
    const port = typeof address === "object" && address ? address.port : 0;
    return {
        server,
        url: `http://127.0.0.1:${port}/`,
        updateOpenInput: (nextOpenInput) => {
            serverState.openInput = nextOpenInput ?? {};
        },
    };
}

copilotSession = await joinSession({
    canvases: [
        createCanvas({
            id: "deployed-instance-canvas",
            displayName: "Deployed instance manager",
            description: "Manage deployed app instances with health checks and lifecycle actions.",
            inputSchema: openInputSchema,
            actions: [
                {
                    name: "configure_instance",
                    description: "Create or update a deployment configuration.",
                    inputSchema: configInputSchema,
                    handler: async (ctx) => {
                        const state = await readState();
                        const config = normalizeConfig(ctx.input);
                        state.instances[config.deploymentId] = config;
                        await writeState(state);
                        return { deploymentId: config.deploymentId, config };
                    },
                },
                    {
                        name: "discover_from_url",
                        description: "Discover Azure Container App metadata from a public URL and save management config.",
                        inputSchema: discoverInputSchema,
                        handler: async (ctx) => {
                            const discovery = await discoverAzureContainerAppByUrl(ctx.input.url);
                            const deploymentId = ctx.input.deploymentId?.trim() || deploymentIdFromUrl(ctx.input.url);
                            const state = await readState();
                            const config = {
                                deploymentId,
                                baseUrl: discovery.baseUrl,
                                statusPath: ctx.input.statusPath ?? "/",
                                startPath: "/start",
                                stopPath: "/stop",
                                restartPath: "/restart",
                                headers: ctx.input.headers ?? {},
                                management: {
                                    kind: "azure-container-app",
                                    appName: discovery.appName,
                                    resourceGroup: discovery.resourceGroup,
                                    subscriptionId: discovery.subscriptionId,
                                    containerAppId: discovery.containerAppId,
                                    discoveredFromUrl: ctx.input.url,
                                },
                            };
                            state.instances[deploymentId] = config;
                            await writeState(state);
                            return { deploymentId, config };
                        },
                    },
                {
                    name: "get_logs",
                    description: "Get recent Azure Container App logs for a deployment.",
                    inputSchema: logsInputSchema,
                    handler: async (ctx) => {
                        const deploymentId = deploymentIdFromInput(ctx.input);
                        const logs = await getAzureLogsForDeployment(deploymentId, {
                            tail: ctx.input.tail ?? 100,
                            type: ctx.input.type ?? "console",
                        });
                        return { deploymentId, ...logs };
                    },
                },
                {
                    name: "analyze_failures",
                    description: "Fetch recent Azure Container App logs and send them to the agent for failure analysis.",
                    inputSchema: analyzeFailuresInputSchema,
                    handler: async (ctx) => {
                        const deploymentId = deploymentIdFromInput(ctx.input);
                        const result = await sendFailureAnalysisToAgent(deploymentId, {
                            tail: ctx.input.tail ?? 200,
                            type: ctx.input.type ?? "console",
                        });
                        return { deploymentId, ...result };
                    },
                },
                {
                    name: "list_instances",
                    description: "List configured deployment IDs and known statuses.",
                    handler: async () => {
                        const state = await readState();
                        const deploymentIds = Object.keys(state.instances);
                        return deploymentIds.map((deploymentId) => ({
                            deploymentId,
                            baseUrl: state.instances[deploymentId].baseUrl,
                            lastCheckedAt: state.statuses[deploymentId]?.checkedAt ?? null,
                        }));
                    },
                },
                {
                    name: "get_instance",
                    description: "Get one deployment configuration and latest status.",
                    inputSchema: deploymentIdInputSchema,
                    handler: async (ctx) => {
                        const deploymentId = deploymentIdFromInput(ctx.input);
                        const { state, instance, deploymentId: resolvedDeploymentId } = await requireInstance(deploymentId);
                        return {
                            deploymentId: resolvedDeploymentId,
                            config: instance,
                            status: state.statuses[resolvedDeploymentId] ?? null,
                        };
                    },
                },
                {
                    name: "refresh_status",
                    description: "Run the configured status endpoint and store the latest response.",
                    inputSchema: timeoutInputSchema,
                    handler: async (ctx) => {
                        const deploymentId = deploymentIdFromInput(ctx.input);
                        const status = await refreshStatusForDeployment(deploymentId, ctx.input.timeoutMs ?? 10000);
                        return { deploymentId, status };
                    },
                },
                {
                    name: "start_instance",
                    description: "Invoke the configured start endpoint.",
                    inputSchema: deploymentIdInputSchema,
                    handler: async (ctx) => {
                        const deploymentId = deploymentIdFromInput(ctx.input);
                        const result = await runLifecycleAction(deploymentId, "start");
                        return { deploymentId, action: "start", ...result };
                    },
                },
                {
                    name: "stop_instance",
                    description: "Invoke the configured stop endpoint.",
                    inputSchema: deploymentIdInputSchema,
                    handler: async (ctx) => {
                        const deploymentId = deploymentIdFromInput(ctx.input);
                        const result = await runLifecycleAction(deploymentId, "stop");
                        return { deploymentId, action: "stop", ...result };
                    },
                },
                {
                    name: "restart_instance",
                    description: "Invoke the configured restart endpoint.",
                    inputSchema: deploymentIdInputSchema,
                    handler: async (ctx) => {
                        const deploymentId = deploymentIdFromInput(ctx.input);
                        const result = await runLifecycleAction(deploymentId, "restart");
                        return { deploymentId, action: "restart", ...result };
                    },
                },
                {
                    name: "delete_instance",
                    description: "Delete a deployment configuration and cached status.",
                    inputSchema: deploymentIdInputSchema,
                    handler: async (ctx) => {
                        const deploymentId = deploymentIdFromInput(ctx.input);
                        const state = await readState();
                        const resolved = await requireInstance(deploymentId);
                        if (!state.instances[resolved.deploymentId]) {
                            throw new CanvasError("instance_not_found", `No deployment config exists for "${deploymentId}".`);
                        }
                        delete state.instances[resolved.deploymentId];
                        delete state.statuses[resolved.deploymentId];
                        await writeState(state);
                        return { deleted: resolved.deploymentId };
                    },
                },
            ],
            open: async (ctx) => {
                const openInput = ctx.input ?? {};
                let entry = servers.get(ctx.instanceId);
                if (!entry) {
                    entry = await startServer(ctx.instanceId, openInput);
                    servers.set(ctx.instanceId, entry);
                } else {
                    entry.updateOpenInput(openInput);
                }
                return {
                    title: openInput.title ?? "Deployed instance manager",
                    status: openInput.url ? `URL: ${openInput.url}` : "No app URL selected",
                    url: entry.url,
                };
            },
            onClose: async (ctx) => {
                const entry = servers.get(ctx.instanceId);
                if (entry) {
                    servers.delete(ctx.instanceId);
                    await new Promise((resolve) => entry.server.close(() => resolve()));
                }
            },
        }),
    ],
});

await copilotSession.log("deployed-instance-canvas loaded", { level: "info", ephemeral: true });
