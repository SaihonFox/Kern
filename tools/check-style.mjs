#!/usr/bin/env node
// Проверка стиля проекта: CRLF, табы, namespace == папка, размер файлов.
// node tools/check-style.mjs          — только проверка (код возврата 1 при нарушениях)
// node tools/check-style.mjs --fix    — исправить CRLF и ведущие пробелы (4 пробела → таб)
import { readFileSync, writeFileSync, readdirSync, statSync, existsSync } from "node:fs";
import { join, dirname, relative, basename, sep } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const fix = process.argv.includes("--fix");
const SKIP = new Set(["reference", "logs", "bin", "obj", "node_modules", ".git", ".vs", "out", "dist", "_out"]);
const TEXT = new Set([".cs", ".csproj", ".props", ".targets", ".slnx", ".kern", ".md", ".json", ".jsonc", ".mjs", ".yml", ".yaml", ".editorconfig"]);
const TABBED = new Set([".cs", ".csproj", ".props", ".targets", ".kern"]);
const MAX_LINES = 800;

const problems = [];
let fixed = 0;

function* walk(dir) {
	for (const name of readdirSync(dir)) {
		if (SKIP.has(name)) continue;
		const p = join(dir, name);
		const st = statSync(p);
		if (st.isDirectory()) yield* walk(p);
		else yield p;
	}
}

function ext(p) {
	const i = p.lastIndexOf(".");
	return i < 0 ? "" : p.slice(i);
}

function projectDirOf(file) {
	let d = dirname(file);
	while (d.length >= root.length) {
		const proj = readdirSync(d).find((f) => f.endsWith(".csproj"));
		if (proj) return { dir: d, name: proj.slice(0, -".csproj".length) };
		if (d === root) break;
		d = dirname(d);
	}
	return null;
}

for (const file of walk(root)) {
	const e = ext(file);
	if (!TEXT.has(e) && basename(file) !== ".editorconfig") continue;
	const rel = relative(root, file);
	let text = readFileSync(file, "utf8");
	let changed = false;

	if (/(^|[^\r])\n/.test(text)) {
		if (fix) { text = text.replace(/\r?\n/g, "\r\n"); changed = true; }
		else problems.push(`${rel}: LF вместо CRLF`);
	}

	if (TABBED.has(e)) {
		const lines = text.split("\r\n");
		lines.forEach((l, i) => {
			if (/^ {4}/.test(l) && !/^ +\*/.test(l)) {
				if (fix) { lines[i] = l.replace(/^( {4})+/, (m) => "\t".repeat(m.length / 4)); changed = true; }
				else problems.push(`${rel}:${i + 1}: ведущие пробелы вместо табов`);
			}
		});
		if (changed) text = lines.join("\r\n");
		if (lines.length > MAX_LINES) problems.push(`${rel}: ${lines.length} строк (> ${MAX_LINES}) — разбейте файл`);
	}

	if (e === ".cs") {
		const m = /^\s*namespace\s+([\w.]+)\s*;/m.exec(text);
		const proj = projectDirOf(file);
		const astSubfolder = /(?:^|[\\/])Syntax[\\/](Core|Declarations|Expressions|Patterns|Statements|Types)[\\/][A-Z][A-Za-z0-9]*\.cs$/i.test(rel);
		if (m && proj && !astSubfolder) {
			const parts = relative(proj.dir, dirname(file)).split(sep).filter(Boolean);
			const expected = [proj.name, ...parts].join(".");
			if (m[1] !== expected) problems.push(`${rel}: namespace ${m[1]} ≠ ожидаемого ${expected}`);
		}
	}

	if (changed) { writeFileSync(file, text); fixed++; }
}

if (fix) console.log(`Исправлено файлов: ${fixed}`);
if (problems.length) {
	console.log(problems.join("\n"));
	console.log(`\nНарушений: ${problems.length}`);
	process.exit(1);
}
console.log("Стиль в порядке.");
