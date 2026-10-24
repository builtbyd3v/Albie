// One command for screen streaming: starts signaling.py and the JPEG fallback (stream.py), opens Chrome,
// and shares the VS Code window over WebRTC.
// No clicks: Chrome auto-picks the capture source named by SCREEN_SOURCE (default: VS Code's front window).
// VS Code not open? It opens VSCODE_FOLDER (default: this repo) and waits for the window; it never shares the
// whole screen unless SCREEN_SOURCE asks for it.
// One-time setup: "window.titleSeparator": " | " in VS Code settings, so Chrome can tell VS Code apart (see below).
//   npm start                                  # share VS Code
//   VSCODE_FOLDER=~/cifar npm start            # open that project if VS Code isn't open yet
//   VSCODE_SIZE=1440x900 npm start             # VS Code's window size in points (default: fill the screen)
//   VSCODE_ZOOM=4 npm start                    # bigger text on the headset (default 3 = 173%)
//   SCREEN_SOURCE="Entire screen" npm start    # share the whole screen
// A Quest plugged in over USB is wired up automatically (adb reverse, see below).
// Ctrl+C stops everything.
import { execFileSync, spawn } from "node:child_process";
import { chromium } from "playwright-core";

const here = new URL(".", import.meta.url).pathname;

// Chrome matches part of a window's name, but not across the "—" in VS Code's default title ("<file> — <repo>").
// With "window.titleSeparator": " | " in VS Code settings the title is "<file> | <repo>", and "| <repo>" matches
// only VS Code (a GitHub tab named after the repo doesn't), and survives switching files.
// VS Code also owns a tiny untitled helper window ("Window", 66x20) that is sometimes its front window, so pick the
// first window whose title has the separator instead of `front window`.
const vsCodeWindow = 'first window of process "Code" whose name contains " | "';

function vsCodeSource() {
  try {
    const script = 'tell application "System Events" to get name of every window of process "Code"';
    const titles = execFileSync("osascript", ["-e", script], { stdio: ["ignore", "pipe", "ignore"] }).toString().trim();
    const title = titles.split(", ").find((t) => t.includes(" | ")) ?? titles.split(", ").find((t) => t !== "Window") ?? "";
    if (!title) return null; // no window yet
    if (title.includes(" | ")) return "| " + title.split(" | ").pop();
    console.warn('VS Code\'s title uses "—", which Chrome can\'t match. Add "window.titleSeparator": " | " to');
    console.warn("VS Code settings (.vscode/settings.json). Matching the repo name instead, which can pick another window.");
    return title.split(" — ").pop() || null;
  } catch {
    return null;
  }
}

// Open VS Code when it has no window, then wait for its title (about 3 s on a cold start).
async function openVsCode() {
  let source = vsCodeSource();
  if (source) return source;
  const folder = process.env.VSCODE_FOLDER || new URL("..", import.meta.url).pathname;
  console.log(`Opening VS Code on ${folder}`);
  execFileSync("open", ["-a", "Visual Studio Code", folder]);
  for (let i = 0; i < 60 && !source; i++) {
    await new Promise((r) => setTimeout(r, 500));
    source = vsCodeSource();
  }
  if (!source) {
    console.error("VS Code didn't open a window in 30 s; not sharing anything. Open it, then restart.");
    process.exit(1);
  }
  return source;
}

// Fills the screen (below the menu bar), so it reads as the real thing on the laptop too. Not macOS full screen: that
// moves VS Code to its own Space, and a window on another Space can't be captured. The stream is capped at 1920 px
// wide, so a full-size window gets shrunk to about half; the extension's 173% zoom keeps the text big on the panel.
// Then the Forge Bit extension closes the side bars and the chat, and sizes the terminal (vscode-bit/extension.js, layout).
function screenArea() {
  const script = 'ObjC.import("AppKit"); const s = $.NSScreen.mainScreen, f = s.frame, v = s.visibleFrame;' +
    "[v.origin.x, f.size.height - v.origin.y - v.size.height, v.size.width, v.size.height].join(' ')";
  return execFileSync("osascript", ["-l", "JavaScript", "-e", script]).toString().trim().split(" ").map(Number);
}

async function fitVsCode() {
  let x = 0, y = 25, width, height;
  if (process.env.VSCODE_SIZE) [width, height] = process.env.VSCODE_SIZE.split("x").map(Number);
  else [x, y, width, height] = screenArea();
  const script = `tell application "System Events" to tell ${vsCodeWindow}
    set position to {${x}, ${y}}
    set size to {${width}, ${height}}
  end tell`;
  try {
    execFileSync("osascript", ["-e", script], { stdio: "ignore" });
  } catch {
    console.warn("Couldn't resize VS Code (full screen, or no Accessibility permission). Streaming it as it is.");
  }
  const url = `http://127.0.0.1:${process.env.BIT_VSCODE_PORT || 8770}/layout`;
  for (let i = 0; i < 20; i++) {  // the extension starts a few seconds after the window on a cold start
    const body = JSON.stringify({ zoom: Number(process.env.VSCODE_ZOOM || 3) });
    const ok = await fetch(url, { method: "POST", headers: { "Content-Type": "application/json" }, body }).then((r) => r.ok, () => false);
    if (ok) return console.log("VS Code laid out for the headset");
    await new Promise((r) => setTimeout(r, 500));
  }
  console.warn("Forge Bit extension didn't answer; side bars stay as they are.");
}

const source = process.env.SCREEN_SOURCE || (await openVsCode());
if (!process.env.SCREEN_SOURCE) await fitVsCode();

// macOS stops drawing a hidden (Cmd+H) or minimized window, and one in full screen lives on its own Space: the capture
// then gets no frames at all, and the headset shows nothing until VS Code comes back. Behind other windows is fine
// (about 55 fps), so put it back there and give the front to whatever app had it.
function keepVsCodeCapturable() {
  const script = `tell application "System Events"
    if not (exists process "Code") then return ""
    set frontApp to name of first process whose frontmost is true
    set why to ""
    if visible of process "Code" is false then
      set visible of process "Code" to true
      set why to "hidden"
    end if
    tell process "Code"
      repeat with w in (windows whose name contains " | ")
        if value of attribute "AXFullScreen" of w is true then
          set value of attribute "AXFullScreen" of w to false
          set why to "full screen"
        end if
        if value of attribute "AXMinimized" of w is true then
          set value of attribute "AXMinimized" of w to false
          set why to "minimized"
        end if
      end repeat
    end tell
    if why is not "" and frontApp is not "Code" then set frontmost of process frontApp to true
    return why
  end tell`;
  try {
    const fixed = execFileSync("osascript", ["-e", script], { stdio: ["ignore", "pipe", "ignore"] }).toString().trim();
    if (fixed) console.log(`VS Code was ${fixed}: put it back behind the front app so the headset keeps getting frames`);
  } catch {
    // no Accessibility permission: nothing to do, the stream just pauses while VS Code is hidden
  }
}
if (!process.env.SCREEN_SOURCE) setInterval(keepVsCodeCapturable, 2000);

// Over USB, `adb reverse` makes localhost:<port> on the Quest reach this laptop, so ForgeNet's default
// hubUrl (localhost) works with no IP to type: 8000 is the hub, 8766 signaling (video itself goes over Wi-Fi),
// 8765 the JPEG fallback, which runs entirely over the cable. Re-run every 5 s to catch a late plug-in.
const adb = process.env.ADB || "/Applications/Unity/Hub/Editor/6000.6.7f1/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb";
let reversed = false;
function reversePorts() {
  try {
    const devices = execFileSync(adb, ["devices"]).toString().split("\n").filter((l) => l.endsWith("\tdevice"));
    if (devices.length === 0) return (reversed = false);
    for (const port of ["8000", "8766", "8765"]) execFileSync(adb, ["reverse", `tcp:${port}`, `tcp:${port}`]);
    if (!reversed) console.log(`adb reverse on ${devices[0].split("\t")[0]}: Quest localhost:8000, :8766 and :8765 reach this laptop`);
    reversed = true;
  } catch {
    // no adb or no device: Wi-Fi only, set ForgeNet's hubUrl on the Quest to this laptop's IP
  }
}
reversePorts();
setInterval(reversePorts, 5000);

const signaling = spawn(`${here}.venv/bin/python`, ["-u", `${here}signaling.py`], { stdio: ["ignore", "pipe", "inherit"] });
// JPEG fallback server (:8765, VS Code's window at Retina resolution), for Unity's JpegScreenReceiver if WebRTC shows no video.
const jpeg = spawn(`${here}.venv/bin/python`, ["-u", `${here}stream.py`], { stdio: ["ignore", "inherit", "inherit"] });
for (const [child, port] of [[signaling, 8766], [jpeg, 8765]])
  child.on("exit", (code) => {
    console.error(`A server exited (${code}). Is another copy already running on :${port}?`);
    process.exit(1);
  });
await new Promise((ready) =>
  signaling.stdout.on("data", (line) => {
    process.stdout.write(line);
    if (line.includes("signaling on")) ready();
  }),
);

const browser = await chromium.launch({
  channel: "chrome", // real Chrome, for H.264; Playwright's Chromium lacks it
  headless: false,
  args: [
    `--auto-select-desktop-capture-source=${source}`,
    "--window-size=480,360",
    // Keep capturing at full rate while the window sits behind others.
    "--disable-backgrounding-occluded-windows",
    "--disable-renderer-backgrounding",
    "--disable-background-timer-throttling",
  ],
});
const page = await browser.newPage();
await page.goto(`file://${here}index.html`);
await page.waitForFunction(() => document.getElementById("status").textContent === "signaling: connected");
await page.click("#share"); // a real click: getDisplayMedia needs a user gesture
console.log(`Sharing "${source}". If Chrome shows a picker instead, the title didn't match; pick the window by hand.`);

let last = "";
setInterval(async () => {
  const status = await page.textContent("#status").catch(() => null);
  if (status && status !== last) console.log((last = status));
}, 1000);
setInterval(async () => {
  const fps = await page.textContent("#fps").catch(() => "");
  if (fps) console.log(fps);
  const encoder = await page.textContent("#encoder").catch(() => "");
  if (encoder) console.log(encoder);
}, 10000);

process.on("SIGINT", async () => {
  for (const child of [signaling, jpeg]) {
    child.removeAllListeners("exit");
    child.kill();
  }
  await browser.close();
  process.exit(0);
});
