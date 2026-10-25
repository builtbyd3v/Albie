// Forge Bit: a tiny local HTTP API so the hub can drive this VS Code window for Bit.
// Uses only the VS Code API, never the mouse or keyboard, so the engineer keeps typing.
//
//   GET  /context                          active file, cursor, selection, visible lines, error count
//   GET  /errors?path=src/x.py             errors + warnings (whole workspace without path)
//   POST /open      { path, line }
//   POST /highlight { path?, start, end, note?, ms? }   glow lines (1-based), optional inline note
//   POST /run       { command, args? }     only ids in forgeBit.allowedCommands
//   POST /train                            types forgeBit.trainCommand into the "Albie" terminal
//   POST /layout    { zoom? }              the headset layout: zoomed editor, Bit's terminal under it, no side bars or chat
//
// It also pushes to the hub (POST <forgeBit.hubUrl>/vscode/event) so Bit can speak up on its own:
//   errors            a saved file has errors (first one + count), or its errors are now fixed (count 0)
//   command_finished  a command in the "Albie" terminal ended, with its exit code
//
// An "Albie" item in the status bar says what Albie is doing in this window, so it shows on the streamed screen too.
//
// Command "Forge: Open this repo in the ML desk" tells the hub which repo is open (POST <hubUrl>/ml/project) and
// follows its setup here: Show diff / Accept / Skip for the training patch, a type picker when the classifier
// isn't sure. The headset's ML desk unfolds from the same hub events.
const http = require("http");
const path = require("path");
const vscode = require("vscode");

let server;
let terminal;
const TERMINAL = "Albie"; // the pet's display name; the extension id and settings stay forge-bit / forgeBit
let statusItem;
let statusTimer;

const glow = vscode.window.createTextEditorDecorationType({
  isWholeLine: true,
  backgroundColor: "rgba(255, 196, 0, 0.22)",
  border: "1px solid rgba(255, 196, 0, 0.8)",
  overviewRulerColor: "rgba(255, 196, 0, 0.9)",
  overviewRulerLane: vscode.OverviewRulerLane.Full,
});
const noteStyle = vscode.window.createTextEditorDecorationType({
  after: { color: "rgba(255, 196, 0, 0.95)", fontStyle: "italic", margin: "0 0 0 2em" },
});
let clearTimer;

function config() {
  return vscode.workspace.getConfiguration("forgeBit");
}

function resolve(p) {
  if (path.isAbsolute(p)) return p;
  const root = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
  if (!root) throw new Error("no workspace folder open");
  return path.join(root, p);
}

function relative(uri) {
  return vscode.workspace.asRelativePath(uri, false);
}

async function show(p, line) {
  const editor = await vscode.window.showTextDocument(vscode.Uri.file(resolve(p)), { preview: false });
  if (line) {
    const pos = new vscode.Position(Math.max(line - 1, 0), 0);
    editor.selection = new vscode.Selection(pos, pos);
    editor.revealRange(new vscode.Range(pos, pos), vscode.TextEditorRevealType.InCenter);
  }
  return editor;
}

function context() {
  const editor = vscode.window.activeTextEditor;
  const errors = vscode.languages.getDiagnostics()
    .reduce((n, [, ds]) => n + ds.filter((d) => d.severity === vscode.DiagnosticSeverity.Error).length, 0);
  if (!editor) return { file: null, errors };
  const { selection, visibleRanges, document } = editor;
  return {
    file: relative(document.uri),
    line: selection.active.line + 1,
    selection: selection.isEmpty ? null : {
      start: selection.start.line + 1,
      end: selection.end.line + 1,
      text: document.getText(selection).slice(0, 2000),
    },
    visible: visibleRanges.length
      ? { start: visibleRanges[0].start.line + 1, end: visibleRanges[visibleRanges.length - 1].end.line + 1 }
      : null,
    lineCount: document.lineCount,
    dirty: document.isDirty,
    errors,
  };
}

function errors(p) {
  const all = p
    ? [[vscode.Uri.file(resolve(p)), vscode.languages.getDiagnostics(vscode.Uri.file(resolve(p)))]]
    : vscode.languages.getDiagnostics();
  const out = [];
  for (const [uri, ds] of all) {
    for (const d of ds) {
      if (d.severity > vscode.DiagnosticSeverity.Warning) continue;
      out.push({
        file: relative(uri),
        line: d.range.start.line + 1,
        severity: d.severity === vscode.DiagnosticSeverity.Error ? "error" : "warning",
        message: d.message,
        source: d.source ?? null,
      });
    }
  }
  return out;
}

async function highlight({ path: p, start, end = start, note, ms = 4000 }) {
  const editor = p ? await show(p, start) : vscode.window.activeTextEditor;
  if (!editor) throw new Error("no editor open");
  const range = new vscode.Range(start - 1, 0, end - 1, 0);
  editor.revealRange(range, vscode.TextEditorRevealType.InCenter);
  editor.setDecorations(glow, [range]);
  const lastLine = editor.document.lineAt(Math.min(end - 1, editor.document.lineCount - 1)).range;
  editor.setDecorations(noteStyle, note ? [{ range: lastLine, renderOptions: { after: { contentText: `← ${note}` } } }] : []);
  clearTimeout(clearTimer);
  clearTimer = setTimeout(() => {
    editor.setDecorations(glow, []);
    editor.setDecorations(noteStyle, []);
  }, ms);
}

async function run({ command, args = [] }) {
  if (!config().get("allowedCommands").includes(command)) throw new Error(`command not allowed: ${command}`);
  return (await vscode.commands.executeCommand(command, ...args)) ?? null;
}

function bitTerminal() {
  if (!terminal || terminal.exitStatus) terminal = vscode.window.createTerminal(TERMINAL);
  terminal.show(true);
  return terminal;
}

function train() {
  const cmd = config().get("trainCommand");
  if (!cmd) throw new Error("set forgeBit.trainCommand first");
  bitTerminal().sendText(cmd);
}

// screen-bridge/start.js calls this once VS Code is up. On the headset the editor is what Bit points at and the
// terminal is where training shows, so the side bar and Copilot chat only cost columns.
// Zoom is per window (not saved to settings): level 2 = 144%, big enough on the panel at 1 m once the full-screen
// window is shrunk to the stream's 1920 px. There is no command to set the panel's height, so: editor to its
// tallest, then 2 steps back, which leaves Bit's terminal a few lines under the code.
async function layout({ zoom = 2 }) {
  const run = (id, times = 1) =>
    Array.from({ length: times }).reduce((p) => p.then(() => vscode.commands.executeCommand(id)), Promise.resolve());
  await run("workbench.action.closeSidebar");
  await run("workbench.action.closeAuxiliaryBar");
  await run("notifications.clearAll");
  await run("workbench.action.zoomReset");
  await run("workbench.action.zoomIn", zoom);
  bitTerminal();
  await run("workbench.action.focusActiveEditorGroup");
  await run("workbench.action.increaseViewHeight", 40);
  await run("workbench.action.decreaseViewHeight", 2);
}

// Highlighted while Bit acts, then back to idle after `ms`.
function status(text, ms = 4000) {
  statusItem.text = `$(sparkle) ${TERMINAL}: ${text}`;
  statusItem.backgroundColor = new vscode.ThemeColor("statusBarItem.warningBackground");
  clearTimeout(statusTimer);
  statusTimer = setTimeout(idle, ms);
}

function idle(text = `$(sparkle) ${TERMINAL}`) {
  statusItem.text = text;
  statusItem.backgroundColor = undefined;
}

const routes = {
  "GET /context": () => context(),
  "GET /errors": (_, query) => errors(query.get("path")),
  "POST /open": (b) => show(b.path, b.line).then(() => (status(`opened ${path.basename(b.path)}`), null)),
  "POST /highlight": (b) => highlight(b).then(() => (status(b.note ?? `looking at line ${b.start}`, b.ms), null)),
  "POST /run": (b) => run(b).then((result) => (status(`ran ${b.command.split(".").pop()}`), { result })),
  "POST /train": () => (train(), status("started training", 6000)),
  "POST /layout": (b) => layout(b).then(() => null),
};

function readBody(req) {
  return new Promise((ok, fail) => {
    let data = "";
    req.on("data", (c) => (data += c));
    req.on("end", () => {
      try { ok(data ? JSON.parse(data) : {}); } catch (e) { fail(e); }
    });
  });
}

function push(event) {
  const req = http.request(new URL("/vscode/event", config().get("hubUrl")), {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    timeout: 2000,
  });
  req.on("error", () => {}); // hub off: Bit just stays quiet
  req.on("timeout", () => req.destroy());
  req.end(JSON.stringify(event));
}

const lastErrors = new Map(); // file -> "line:message:count" last pushed, so re-saves stay quiet

// Only on save: while typing, half-finished lines are errors and Bit would nag.
function errorsOnSave(doc) {
  if (doc.uri.scheme !== "file") return;
  // ponytail: 1.5 s lets language servers re-check the saved file before we read diagnostics.
  setTimeout(() => {
    const file = relative(doc.uri);
    const errs = vscode.languages.getDiagnostics(doc.uri)
      .filter((d) => d.severity === vscode.DiagnosticSeverity.Error);
    if (errs.length) {
      const [d] = errs;
      const line = d.range.start.line + 1;
      const key = `${line}:${d.message}:${errs.length}`;
      if (lastErrors.get(file) === key) return;
      lastErrors.set(file, key);
      push({ kind: "errors", file, line, message: d.message, count: errs.length });
    } else if (lastErrors.delete(file)) {
      push({ kind: "errors", file, count: 0 }); // fixed: Bit can say "nice"
    }
  }, 1500);
}

function hub(method, route, body) {
  return new Promise((resolve, reject) => {
    const req = http.request(new URL(route, config().get("hubUrl")), {
      method,
      headers: { "Content-Type": "application/json" },
      timeout: 10000,
    }, (res) => {
      let text = "";
      res.on("data", (chunk) => { text += chunk; });
      res.on("end", () => {
        let data = {};
        try { data = text ? JSON.parse(text) : {}; } catch { data = { detail: text }; }
        if (res.statusCode >= 400) reject(new Error(data.detail || `hub answered ${res.statusCode}`));
        else resolve(data);
      });
    });
    req.on("error", (e) => reject(new Error(`can't reach the Forge hub at ${config().get("hubUrl")} (${e.message})`)));
    req.on("timeout", () => req.destroy(new Error("the Forge hub didn't answer")));
    req.end(body === undefined ? undefined : JSON.stringify(body));
  });
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// Poll GET /ml/project until setup stops: ready, failed, needs_approval or confirm_type (about a minute or two).
async function setupResult() {
  for (let i = 0; i < 150; i++) {
    const state = await hub("GET", "/ml/project");
    if (["ready", "failed", "needs_approval", "confirm_type"].includes(state.status)) return state;
    status(`$(sync~spin) ML desk: ${state.status}…`, 5000);
    await sleep(2000);
  }
  throw new Error("setup is taking over 5 minutes; check the hub");
}

async function openInMlDesk() {
  const root = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
  if (!root) return vscode.window.showWarningMessage("Forge: open a folder first.");
  try {
    status(`$(beaker) ML desk: opening ${path.basename(root)}`, 5000);
    await hub("POST", "/ml/project", { path: root });
    let state = await setupResult();
    if (state.status === "confirm_type") {
      const pick = await vscode.window.showQuickPick(state.candidates || [], {
        placeHolder: `Forge isn't sure what ${path.basename(root)} is. Which type is it?`,
      });
      if (!pick) return;
      await hub("POST", "/ml/project/type", { archetype: pick });
      state = await setupResult();
    }
    if (state.status === "needs_approval") {
      const files = (state.patch?.files || []).map((f) => f.path || f).join(", ");
      for (;;) {
        const choice = await vscode.window.showInformationMessage(
          `${state.summary || "Forge"}: hook up training with a few lines in ${files || "the training script"}?`,
          "Show diff", "Accept", "Skip");
        if (choice === "Show diff") { await hub("POST", "/ml/project/diff", {}); continue; }
        if (!choice) return;
        await hub("POST", "/ml/project/patch", { accept: choice === "Accept" });
        state = await hub("GET", "/ml/project");
        break;
      }
    }
    if (state.status === "failed") throw new Error(state.error || "setup failed");
    status(`$(check) ML desk: ${state.summary || "ready"}`, 8000);
    vscode.window.showInformationMessage(`Forge ML desk ready: ${state.summary || path.basename(root)}`);
  } catch (e) {
    idle();
    vscode.window.showErrorMessage(`Forge ML desk: ${e.message}`);
  }
}

function watch(ctx) {
  ctx.subscriptions.push(
    vscode.workspace.onDidSaveTextDocument(errorsOnSave),
    vscode.window.onDidEndTerminalShellExecution((e) => {
      if (e.terminal.name !== TERMINAL) return;
      push({ kind: "command_finished", command: e.execution.commandLine.value, exit_code: e.exitCode ?? null });
    }),
  );
}

function activate(ctx) {
  statusItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 100);
  statusItem.tooltip = "Forge Bit: the Forge pet can open, highlight and run things in this window";
  idle();
  statusItem.show();
  watch(ctx);
  const port = config().get("port");
  server = http.createServer(async (req, res) => {
    const url = new URL(req.url, "http://127.0.0.1");
    const route = routes[`${req.method} ${url.pathname}`];
    const send = (status, body) => {
      res.writeHead(status, { "Content-Type": "application/json" });
      res.end(JSON.stringify(body ?? { ok: true }));
    };
    if (!route) return send(404, { error: `no route ${req.method} ${url.pathname}` });
    try {
      send(200, await route(await readBody(req), url.searchParams));
    } catch (e) {
      send(400, { error: e.message });
    }
  });
  // ponytail: a second VS Code window just logs the port clash; Bit drives the first window.
  server.on("error", (e) => {
    console.error(`[forge-bit] ${e.message}`);
    idle(`$(circle-slash) ${TERMINAL}: busy in another window`);
  });
  server.listen(port, "127.0.0.1");
  ctx.subscriptions.push(glow, noteStyle, statusItem, { dispose: () => server.close() },
    vscode.commands.registerCommand("forgeBit.openInMlDesk", openInMlDesk));
}

function deactivate() {
  server?.close();
}

module.exports = { activate, deactivate };
