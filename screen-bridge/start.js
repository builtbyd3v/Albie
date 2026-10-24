// One command for WebRTC streaming: starts signaling.py, opens Chrome, and shares the VS Code window.
// No clicks: Chrome auto-picks the capture source named by SCREEN_SOURCE (default: VS Code's front window).
//   npm start                                  # share VS Code
//   SCREEN_SOURCE="Entire screen" npm start    # share the whole screen
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

const signaling = spawn(`${here}.venv/bin/python`, ["-u", `${here}signaling.py`], { stdio: ["ignore", "pipe", "inherit"] });
signaling.on("exit", (code) => {
  console.error(`signaling.py exited (${code}). Is another copy already running on :8766?`);
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
  signaling.removeAllListeners("exit");
  signaling.kill();
  await browser.close();
  process.exit(0);
});
