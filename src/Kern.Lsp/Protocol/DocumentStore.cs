using Kern.Compiler.Compilation;

namespace Kern.Lsp.Protocol;

/// <summary>
/// Открытые документы LSP: URI → текст. Открытые файлы образуют один проект;
/// путь относительно корня рабочей папки становится именем модуля.
/// </summary>
public sealed class DocumentStore
{
	private readonly Dictionary<string, string> _documents = new(StringComparer.Ordinal);

	public string? RootPath { get; private set; }

	public void SetRoot(string? rootUri)
	{
		if (string.IsNullOrWhiteSpace(rootUri))
		{
			RootPath = null;
			return;
		}

		RootPath = UriToPath(rootUri);
	}

	public void Open(string uri, string text) => _documents[uri] = text;

	public void Change(string uri, string text)
	{
		if (_documents.ContainsKey(uri))
			_documents[uri] = text;
	}

	public void Close(string uri) => _documents.Remove(uri);

	public string? Text(string uri) => _documents.TryGetValue(uri, out string? text) ? text : null;

	public IReadOnlyList<string> Uris => [.. _documents.Keys];

	public IReadOnlyList<ProjectSourceFile> ToProject() =>
	[.. _documents
		.OrderBy(pair => RelativePath(pair.Key), StringComparer.Ordinal)
		.Select(pair => new ProjectSourceFile(RelativePath(pair.Key), pair.Value))];

	public string RelativePath(string uri)
	{
		string full = UriToPath(uri);
		if (RootPath is { Length: > 0 } root &&
			full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
			return full[(root.Length + 1)..].Replace('\\', '/');
		return Path.GetFileName(full);
	}

	public string? UriForRelativePath(string relativePath)
	{
		string normalized = relativePath.Replace('\\', '/');
		foreach (string uri in _documents.Keys)
		{
			if (string.Equals(RelativePath(uri), normalized, StringComparison.OrdinalIgnoreCase))
				return uri;
		}

		return null;
	}

	private static string UriToPath(string uri)
	{
		if (Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed) && parsed.IsFile)
			return Path.GetFullPath(parsed.LocalPath);
		return uri;
	}
}
