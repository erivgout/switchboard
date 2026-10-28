// Usage:
//   node tools/ui-snap/snap.mjs --base http://localhost:5080 --user sam@switchboard.local \
//        [--theme light|dark] [--width 1440] [--height 900] [--mobile] [--out .artifacts/snaps] /tickets /tickets/1003 ...
// Prints one PNG path per page. Use "--user none" for signed-out pages.
import { chromium } from "playwright-core";
import fs from "node:fs";
import path from "node:path";

const args = process.argv.slice(2);
const opt = { base: "http://localhost:5080", user: "sam@switchboard.local", password: "Switchboard!2026", theme: "light", width: 1440, height: 900, out: ".artifacts/snaps", mobile: false, full: true };
const pages = [];
for (let i = 0; i < args.length; i++) {
  const a = args[i];
  if (a === "--mobile") { opt.mobile = true; continue; }
  if (a === "--viewport-only") { opt.full = false; continue; }
  if (a.startsWith("--")) { opt[a.slice(2)] = args[++i]; continue; }
  // Git Bash rewrites "/tickets" to "C:/Program Files/Git/tickets"; undo that.
  pages.push(a.replace(/^[A-Za-z]:\/.*?\/Git(?=\/)/, ""));
}
if (pages.length === 0) pages.push("/");

const candidates = [
  process.env.SB_BROWSER,
  "C:/Program Files/Google/Chrome/Application/chrome.exe",
  "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
  "/usr/bin/google-chrome", "/usr/bin/chromium", "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
].filter(Boolean);
const executablePath = candidates.find((p) => fs.existsSync(p));
if (!executablePath) { console.error("No Chrome/Edge found. Set SB_BROWSER to a Chromium executable."); process.exit(2); }

const browser = await chromium.launch({ executablePath, headless: true });
const context = await browser.newContext({
  viewport: opt.mobile ? { width: 390, height: 844 } : { width: +opt.width, height: +opt.height },
  deviceScaleFactor: opt.mobile ? 2 : 1,
  colorScheme: opt.theme === "dark" ? "dark" : "light",
});
await context.addInitScript((theme) => { try { localStorage.setItem("sb-theme", theme); } catch (e) {} }, opt.theme);
const page = await context.newPage();
const errors = [];
page.on("console", (m) => { if (m.type() === "error") errors.push(m.text()); });
page.on("pageerror", (e) => errors.push(String(e)));

if (opt.user !== "none") {
  await page.goto(`${opt.base}/account/login`);
  await page.fill('input[name="Email"]', opt.user);
  await page.fill('input[name="Password"]', opt.password);
  await Promise.all([page.waitForLoadState("networkidle"), page.click('button[type="submit"]')]);
  if (page.url().includes("/account/login")) { console.error("Sign-in failed for " + opt.user); process.exit(3); }
}

fs.mkdirSync(opt.out, { recursive: true });
for (const p of pages) {
  const response = await page.goto(opt.base + p, { waitUntil: "networkidle" });
  await page.waitForTimeout(250);
  const name = (p.replace(/[^a-z0-9]+/gi, "_").replace(/^_|_$/g, "") || "home") + `-${opt.theme}${opt.mobile ? "-mobile" : ""}.png`;
  const file = path.resolve(opt.out, name);
  await page.screenshot({ path: file, fullPage: opt.full });
  console.log(`${response?.status()} ${p} -> ${file}`);
}
if (errors.length) { console.log("Browser console errors:"); for (const e of errors) console.log("  " + e); }
await browser.close();
