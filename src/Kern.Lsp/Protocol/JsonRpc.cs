using System.Text;

namespace Kern.Lsp.Protocol;

/// <summary>
/// JSON-RPC 2.0 поверх stdio с заголовком <c>Content-Length</c> (LSP base protocol).
/// Чтение и запись — синхронные: сервер однопоточный, как и VM.
/// </summary>
public static class JsonRpc
{
	/// <summary>Прочитать одно сообщение; <c>null</c> — поток закончился.</summary>
	public static string? ReadMessage(Stream input)
	{
		ArgumentNullException.ThrowIfNull(input);
		var header = new StringBuilder();
		while (true)
		{
			int next = input.ReadByte();
			if (next < 0)
				return null;
			header.Append((char)next);
			if (header.Length >= 4 &&
				header[^4] == '\r' && header[^3] == '\n' && header[^2] == '\r' && header[^1] == '\n')
				break;
			if (header.Length >= 2 && header[^2] == '\n' && header[^1] == '\n')
				break;
		}

		int length = 0;
		foreach (string line in header.ToString().Split('\n'))
		{
			string trimmed = line.Trim('\r', ' ');
			if (!trimmed.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
				continue;
			string value = trimmed["Content-Length:".Length..].Trim();
			_ = int.TryParse(value, out length);
		}

		if (length <= 0)
			return string.Empty;

		byte[] buffer = new byte[length];
		int read = 0;
		while (read < length)
		{
			int chunk = input.Read(buffer, read, length - read);
			if (chunk <= 0)
				return null;
			read += chunk;
		}

		return Encoding.UTF8.GetString(buffer);
	}

	/// <summary>Записать сообщение с заголовком <c>Content-Length</c>.</summary>
	public static void WriteMessage(Stream output, string json)
	{
		ArgumentNullException.ThrowIfNull(output);
		ArgumentNullException.ThrowIfNull(json);
		byte[] body = Encoding.UTF8.GetBytes(json);
		byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
		output.Write(header);
		output.Write(body);
		output.Flush();
	}
}
