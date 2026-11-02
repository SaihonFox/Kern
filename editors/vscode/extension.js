const fs = require('fs');
const path = require('path');
const { workspace, window, commands } = require('vscode');
const { LanguageClient, TransportKind } = require('vscode-languageclient/node');

let client;

function isFile(candidate) {
	try {
		return fs.statSync(candidate).isFile();
	} catch {
		return false;
	}
}

function executableName() {
	return process.platform === 'win32' ? 'kernc.exe' : 'kernc';
}

function repositoryRoots(context) {
	const roots = new Set();
	for (const folder of workspace.workspaceFolders || [])
		roots.add(folder.uri.fsPath);
	// editors/vscode -> корень репозитория Kern
	roots.add(path.resolve(context.extensionPath, '..', '..'));

	const result = new Set();
	for (const root of roots) {
		let current = root;
		while (current && current !== path.dirname(current)) {
			if (isFile(path.join(current, 'Kern.slnx')))
				result.add(current);
			current = path.dirname(current);
		}
		result.add(root);
	}
	return [...result];
}

/** Найти собранный kernc: настройка → bin/Release|Debug репозитория → PATH. */
function resolveServer(context) {
	const configuration = workspace.getConfiguration('kern');
	const configured = (configuration.get('lsp.command') || '').trim();
	const args = configuration.get('lsp.args') || ['lsp'];
	if (configured.length > 0)
		return { command: configured, args };

	const executable = executableName();
	for (const root of repositoryRoots(context)) {
		for (const configurationName of ['Release', 'Debug']) {
			const candidate = path.join(
				root, 'src', 'Kern.Cli', 'bin', configurationName, 'net10.0', executable);
			if (isFile(candidate))
				return { command: candidate, args };
		}
	}

	// Последняя попытка: kernc может быть установлен в PATH.
	return { command: executable.replace(/\.exe$/i, ''), args };
}

async function activate(context) {
	const server = resolveServer(context);
	const serverOptions = {
		command: server.command,
		args: server.args,
		transport: TransportKind.stdio,
		options: {
			cwd: workspace.workspaceFolders?.[0]?.uri.fsPath
		}
	};
	const clientOptions = {
		documentSelector: [{ scheme: 'file', language: 'kern' }],
		synchronize: {
			fileEvents: workspace.createFileSystemWatcher('**/*.kern')
		}
	};

	client = new LanguageClient('kern-lsp', 'Kern Language Server', serverOptions, clientOptions);
	try {
		await client.start();
		context.subscriptions.push(client);
	} catch (error) {
		const message =
			`Kern LSP: не удалось запустить '${server.command}'. ` +
			'Соберите kernc (dotnet build src/Kern.Cli/Kern.Cli.csproj -c Release) ' +
			'или укажите полный путь в настройке kern.lsp.command.';
		window.showErrorMessage(message, 'Открыть настройки').then(action => {
			if (action === 'Открыть настройки')
				commands.executeCommand('workbench.action.openSettings', 'kern.lsp');
		});
		throw error;
	}
}

function deactivate() {
	return client ? client.stop() : undefined;
}

module.exports = { activate, deactivate };
