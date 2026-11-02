const { workspace } = require('vscode');
const { LanguageClient, TransportKind } = require('vscode-languageclient/node');

let client;

function activate(context) {
	const configuration = workspace.getConfiguration('kern');
	const command = configuration.get('lsp.command') || 'kernc';
	const args = configuration.get('lsp.args') || ['lsp'];

	const serverOptions = {
		command,
		args,
		transport: TransportKind.stdio
	};
	const clientOptions = {
		documentSelector: [{ scheme: 'file', language: 'kern' }],
		synchronize: {
			fileEvents: workspace.createFileSystemWatcher('**/*.kern')
		}
	};

	client = new LanguageClient('kern-lsp', 'Kern Language Server', serverOptions, clientOptions);
	context.subscriptions.push(client.start());
}

function deactivate() {
	return client ? client.stop() : undefined;
}

module.exports = { activate, deactivate };
