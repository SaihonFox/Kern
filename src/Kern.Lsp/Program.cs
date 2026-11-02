using System.Text;
using Kern.Lsp;
using Kern.Lsp.Protocol;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = new UTF8Encoding(false);

using Stream input = Console.OpenStandardInput();
using Stream output = Console.OpenStandardOutput();

var server = new LspServer(json => JsonRpc.WriteMessage(output, json));
while (!server.ExitRequested)
{
	string? message = JsonRpc.ReadMessage(input);
	if (message is null)
		break;
	string? response = server.Handle(message);
	if (response is not null)
		JsonRpc.WriteMessage(output, response);
}
