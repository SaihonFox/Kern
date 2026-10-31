#!/usr/bin/env node
// Определяет, как устроен API вашего посредника (стиль OpenAI или Anthropic) и какие модели доступны.
// Ключ берётся из переменной окружения MODULEAPI_API_KEY и никуда не записывается и не печатается.
//
// PowerShell:   $env:MODULEAPI_API_KEY = "sk-..."
//               node tools/probe-provider.mjs https://адрес-посредника.example
//               node tools/probe-provider.mjs https://адрес-посредника.example --chat claude-sonnet-5-5
const args = process.argv.slice(2);
const baseArg = args.find((a) => !a.startsWith("--"));
const chatIdx = args.indexOf("--chat");
const chatModel = chatIdx >= 0 ? args[chatIdx + 1] : null;
const key = process.env.MODULEAPI_API_KEY;

if (!baseArg || !key) {
	console.error("Нужны: адрес (аргумент) и ключ в переменной окружения MODULEAPI_API_KEY. См. комментарий в начале файла.");
	process.exit(1);
}

const base = baseArg.replace(/\/+$/, "");
const roots = [...new Set([base, base.endsWith("/v1") ? base : base + "/v1"])];

async function call(url, init) {
	try {
		const r = await fetch(url, { ...init, signal: AbortSignal.timeout(30000) });
		const text = await r.text();
		let json = null;
		try { json = JSON.parse(text); } catch { /* не JSON */ }
		return { status: r.status, json, text };
	} catch (e) {
		return { status: 0, json: null, text: String(e) };
	}
}

const found = [];
for (const root of roots) {
	const openai = await call(`${root}/models`, { headers: { Authorization: `Bearer ${key}` } });
	console.log(`GET ${root}/models  (Bearer)       → HTTP ${openai.status}`);
	if (openai.status === 200 && openai.json?.data) found.push({ style: "openai", root, models: openai.json.data.map((m) => m.id) });

	const anth = await call(`${root}/models`, { headers: { "x-api-key": key, "anthropic-version": "2023-06-01" } });
	console.log(`GET ${root}/models  (x-api-key)    → HTTP ${anth.status}`);
	if (anth.status === 200 && anth.json?.data && !found.some((f) => f.root === root && f.style === "anthropic")) {
		found.push({ style: "anthropic", root, models: anth.json.data.map((m) => m.id) });
	}
}

if (!found.length) {
	console.log("\nНи один вариант не ответил 200. Проверьте адрес в документации посредника (ищите «base URL» / «API endpoint»), ключ и доступ к сети.");
	process.exit(2);
}

for (const f of found) {
	const claude = f.models.filter((m) => /claude|opus|sonnet|haiku|fable/i.test(m));
	console.log(`\n=== Стиль: ${f.style}   baseURL: ${f.root}`);
	console.log(`Всего моделей: ${f.models.length}. Похожие на Claude:\n  ${(claude.length ? claude : f.models.slice(0, 30)).join("\n  ")}`);
}

if (chatModel) {
	const f = found[0];
	const res = f.style === "openai"
		? await call(`${f.root}/chat/completions`, {
			method: "POST",
			headers: { Authorization: `Bearer ${key}`, "content-type": "application/json" },
			body: JSON.stringify({ model: chatModel, max_tokens: 16, messages: [{ role: "user", content: "Ответь одним словом: ок" }] }),
		})
		: await call(`${f.root}/messages`, {
			method: "POST",
			headers: { "x-api-key": key, "anthropic-version": "2023-06-01", "content-type": "application/json" },
			body: JSON.stringify({ model: chatModel, max_tokens: 16, messages: [{ role: "user", content: "Ответь одним словом: ок" }] }),
		});
	console.log(`\nТестовый запрос к ${chatModel} (${f.style}) → HTTP ${res.status}`);
	console.log(res.text.slice(0, 400));
}

console.log("\nВпишите найденные baseURL и id моделей в models.json (см. SETUP.md).");
