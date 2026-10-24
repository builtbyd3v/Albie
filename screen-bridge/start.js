// One command for screen streaming: starts signaling.py and the JPEG fallback (stream.py), opens Chrome,
// and shares the VS Code window over WebRTC.
// No clicks: Chrome auto-picks the capture source named by SCREEN_SOURCE (default: VS Code's front window).
//   npm start                                  # share VS Code
//   SCREEN_SOURCE="Entire screen" npm start    # share the whole screen
// A Quest plugged in over USB is wired up automatically (adb reverse, see below).
// Ctrl+C stops everything.
import { execFileSync, spawn } from "node:child_process";
import { chromium } from "playwright-core";

const here = new URL(".", import.meta.url).pathname;

// VS Code's title is "<file> — <workspace>". Chrome matches part of a source name but not the full
// title (the "—" seems to break it), so match the workspace name, which also survives switching files.
function vsCodeWorkspace() {
  try {
    const script = 'tell application "System Events" to get name of front window of process "Code"';
    const title = execFileSync("osascript", ["-e", script], { stdio: ["ignore", "pipe", "ignore"] }).toString().trim();
    return title.split(" — ").pop() || null;
  } catch {
    return null;
  }
}

const source = process.env.SCREEN_SOURCE || vsCodeWorkspace() || "Entire screen";

// Over USB, `adb reverse` makes localhost:<port> on the Quest reach this laptop, so the panel's default
// ws://localhost:8766 works with no IP to type. Only signaling (TCP) needs it; video goes over Wi-Fi.
// 8765 is the JPEG fallback, which runs entirely over the cable. Re-run every 5 s to catch a late plug-in.
const adb = process.env.ADB || "/Applications/Unity/Hub/Editor/6000.6.7f1/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb";
let reversed = false;
function reversePorts() {
  try {
    const devices = execFileSync(adb, ["devices"]).toString().split("\n").filter((l) => l.endsWith("\tdevice"));
    if (devices.length === 0) return (reversed = false);
    for (const port of ["8766", "8765"]) execFileSync(adb, ["reverse", `tcp:${port}`, `tcp:${port}`]);
    if (!reversed) console.log(`adb reverse on ${devices[0].split("\t")[0]}: Quest localhost:8766 and :8765 reach this laptop`);
    reversed = true;
  } catch {
    // no adb or no device: Wi-Fi only, set signalingUrl to this laptop's IP
  }
}
reversePorts();
setInterval(reversePorts, 5000);

const signaling = spawn(`${here}.venv/bin/python`, ["-u", `${here}signaling.py`], { stdio: ["ignore", "pipe", "inherit"] });
// JPEG fallback server (whole screen, :8765), for Unity's JpegScreenReceiver if WebRTC shows no video.
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
}, 10000);

process.on("SIGINT", async () => {
  for (const child of [signaling, jpeg]) {
    child.removeAllListeners("exit");
    child.kill();
  }
  await browser.close();
  process.exit(0);
});
