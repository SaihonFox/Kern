#!/usr/bin/env node
// Генерирует .opencode/agents/*.md и opencode.jsonc из models.json + agents-src/.
// Запуск (из корня проекта): node tools/apply-models.mjs
// Формат конфигов opencode: "v1" (permission/maxSteps) или "v2" (permissions-список). Задаётся в models.json ("format").
import { readFileSync, writeFileSync, mkdirSync, readdirSync, rmSync, existsSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const cfg = JSON.parse(readFileSync(join(root, "models.json"), "utf8"));
const defs = JSON.parse(readFileSync(join(root, "agents-src", "agents.json"), "utf8"));
const format = cfg.format === "v2" ? "v2" : "v1";
const NL = "\r\n";

const models = cfg.models ?? {};
for (const role of ["lead", "dev", "cheap"]) {
	if (!models[role] || models[role].includes("PROVIDER")) {
		console.error(`models.json: роль "${role}" не заполнена (сейчас: ${models[role] ?? "нет"}). Выполните "opencode models" и впишите id вида provider/model.`);
		process.exit(1);
	}
}

const q = (s) => JSON.stringify(s);

function permissionV1(def) {
	const lines = ["permission:", `  edit: ${def.edit}`, "  bash:"];
	for (const [k, v] of Object.entries(defs.common.bash)) lines.push(`    ${q(k)}: ${v}`);
	return lines;
}

function permissionV2(def) {
	const lines = ["permissions:", "  - action: edit", '    resource: "*"', `    effect: ${def.edit}`];
	for (const [k, v] of Object.entries(defs.common.bash)) {
		lines.push("  - action: shell", `    resource: ${q(k)}`, `    effect: ${v}`);
	}
	return lines;
}

const outDir = join(root, ".opencode", "agents");
mkdirSync(outDir, { recursive: true });
for (const f of readdirSync(outDir)) if (f.endsWith(".md")) rmSync(join(outDir, f));

for (const a of defs.agents) {
	const body = readFileSync(join(root, "agents-src", `${a.name}.md`), "utf8").replace(/\r\n/g, "\n").trim();
	const fm = ["---", `description: ${q(a.description)}`, `mode: ${a.mode}`, `model: ${models[a.role]}`];
	if (format === "v1") fm.push(`temperature: ${a.temperature}`);
	fm.push(`steps: ${a.steps}`);
	fm.push(...(format === "v1" ? permissionV1(a) : permissionV2(a)));
	fm.push("---", "");
	writeFileSync(join(outDir, `${a.name}.md`), fm.join(NL) + body.replace(/\n/g, NL) + NL);
}

// opencode.jsonc
const conf = { $schema: "https://opencode.ai/config.json", model: models.dev, small_model: models.cheap };
if (cfg.provider) {
	const p = cfg.provider;
	conf.provider = {
		[p.id]: { npm: p.npm ?? "@ai-sdk/openai-compatible", name: p.name ?? p.id, options: { baseURL: p.baseURL }, models: p.models ?? {} },
	};
}
const text =
	"// Сгенерировано tools/apply-models.mjs — правьте models.json и запускайте скрипт заново." + NL +
	"// Ключ API здесь НЕ хранится: он вводится через `opencode auth login` (лежит в ~/.local/share/opencode/auth.json)." + NL +
	JSON.stringify(conf, null, "\t").replace(/\n/g, NL) + NL;
writeFileSync(join(root, "opencode.jsonc"), text);

console.log(`Готово (формат ${format}): ${defs.agents.length} агентов в .opencode/agents, opencode.jsonc обновлён.`);
console.log(`lead=${models.lead}  dev=${models.dev}  cheap=${models.cheap}`);
