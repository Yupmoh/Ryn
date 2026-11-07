import { copyFile, readFile, writeFile } from "node:fs/promises";
import { build } from "vite";

await build();
await copyFile("THIRD-PARTY-NOTICES.txt", "../wwwroot/THIRD-PARTY-NOTICES.txt");
const bundlePath = "../wwwroot/assets/terminal-app.js";
const bundle = await readFile(bundlePath, "utf8");
const notice = "/*! Ryn TerminalApp bundle; see ../THIRD-PARTY-NOTICES.txt for licenses. */\n";
if (!bundle.startsWith(notice)) await writeFile(bundlePath, notice + bundle);
