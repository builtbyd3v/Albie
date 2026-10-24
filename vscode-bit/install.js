// Links this folder into ~/.vscode/extensions so VS Code loads Forge Bit. Runs before `npm start` in
// screen-bridge (prestart); safe to run any time. Prints only when it changed something.
const fs = require("fs");
const os = require("os");
const path = require("path");
const { version } = require("./package.json");

const dir = path.join(os.homedir(), ".vscode", "extensions");
const link = path.join(dir, `forge.forge-bit-${version}`);

let current = null;
try {
  current = fs.readlinkSync(link);
} catch {}
if (current === __dirname) process.exit(0);

fs.mkdirSync(dir, { recursive: true });
// Old versions or another checkout: drop our symlinks only, never a real folder.
for (const name of fs.readdirSync(dir)) {
  const p = path.join(dir, name);
  if (name.startsWith("forge.forge-bit-") && fs.lstatSync(p).isSymbolicLink()) fs.unlinkSync(p);
}
fs.symlinkSync(__dirname, link, "dir");
console.log(`[forge-bit] Linked ${link} -> ${__dirname}. Relaunch VS Code once to load it.`);
