using System.IO.Compression;
using System.Security.Cryptography;

namespace Kern.Cli.Build;

/// <summary>
/// Загрузка минимальных удалённых пакетов (docs/BUILDER.md §8): zip по HTTPS с
/// обязательным SHA-256, распаковка в <c>~/.kern/pkgs/&lt;sha256&gt;/</c> и повторное
/// использование кэша. Реестра и разрешения версий нет.
/// </summary>
internal sealed class PackageFetcher
{
	private const int MaxRedirects = 5;
	private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

	private readonly HttpClient _client;
	private readonly string _cacheRoot;

	public PackageFetcher(HttpMessageHandler? handler = null, string? cacheRoot = null)
	{
		_client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
		{
			Timeout = Timeout,
		};
		_cacheRoot = cacheRoot ?? DefaultCacheRoot();
	}

	/// <summary>Скачать и распаковать пакет; вернуть корень содержимого (без лишней папки).</summary>
	public string Fetch(PackageModel package)
	{
		Validate(package);
		string hash = package.Sha256.Trim().ToLowerInvariant();
		string directory = Path.Combine(_cacheRoot, hash);
		if (File.Exists(Marker(directory)))
			return ContentRoot(directory);

		Uri uri = new(package.Url, UriKind.Absolute);
		byte[] archive = Download(package, uri);
		string actual = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
		if (!string.Equals(actual, hash, StringComparison.Ordinal))
			throw new BuildException(
				$"пакет '{package.Name}': SHA-256 не совпадает (ожидался {hash}, получен {actual})");

		try
		{
			Directory.CreateDirectory(_cacheRoot);
			string temporary = Path.Combine(_cacheRoot, "tmp-" + Guid.NewGuid().ToString("N"));
			try
			{
				Extract(archive, temporary);
				WriteCompleteMarker(temporary);
				if (File.Exists(Marker(directory)))
					Directory.Delete(temporary, recursive: true);
				else
				{
					if (Directory.Exists(directory))
						Directory.Delete(directory, recursive: true);
					Directory.Move(temporary, directory);
				}
			}
			catch
			{
				TryDelete(temporary);
				throw;
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			throw new BuildException($"пакет '{package.Name}': не удалось распаковать архив: {exception.Message}");
		}

		return ContentRoot(directory);
	}

	/// <summary>Корень кэша пакетов: <c>KERN_HOME</c> или <c>~/.kern</c>.</summary>
	public static string DefaultCacheRoot()
	{
		string home = Environment.GetEnvironmentVariable("KERN_HOME") is { Length: > 0 } custom
			? custom
			: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kern");
		return Path.Combine(home, "pkgs");
	}

	private static void Validate(PackageModel package)
	{
		if (string.IsNullOrWhiteSpace(package.Name))
			throw new BuildException("пакет: пустое имя");
		if (string.IsNullOrWhiteSpace(package.Url) ||
			!Uri.TryCreate(package.Url, UriKind.Absolute, out Uri? uri))
			throw new BuildException($"пакет '{package.Name}': неверный URL '{package.Url}'");
		if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
			throw new BuildException(
				$"пакет '{package.Name}': разрешён только https (получен '{uri.Scheme}')");
		if (!IsSha256(package.Sha256))
			throw new BuildException(
				$"пакет '{package.Name}': sha256 должен быть 64 hex-символами");
	}

	private static bool IsSha256(string value)
	{
		string text = value.Trim();
		if (text.Length != 64)
			return false;
		foreach (char symbol in text)
			if (!char.IsAsciiHexDigit(symbol))
				return false;
		return true;
	}

	/// <summary>Загрузка с ручными переходами: каждый шаг обязан оставаться https.</summary>
	private byte[] Download(PackageModel package, Uri uri)
	{
		for (var redirect = 0; redirect <= MaxRedirects; redirect++)
		{
			if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
				throw new BuildException(
					$"пакет '{package.Name}': перенаправление на не-https адрес '{uri}' запрещено");

			HttpResponseMessage response;
			try
			{
				using var request = new HttpRequestMessage(HttpMethod.Get, uri);
				response = _client.Send(request, HttpCompletionOption.ResponseHeadersRead);
			}
			catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
			{
				throw new BuildException($"пакет '{package.Name}': не удалось скачать {uri}: {exception.Message}");
			}

			using (response)
			{
				if ((int)response.StatusCode is >= 300 and < 400)
				{
					if (response.Headers.Location is not { } location)
						throw new BuildException(
							$"пакет '{package.Name}': перенаправление без адреса от {uri}");
					uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
					continue;
				}
				if (!response.IsSuccessStatusCode)
					throw new BuildException(
						$"пакет '{package.Name}': {uri} вернул {(int)response.StatusCode} {response.ReasonPhrase}");

				try
				{
					using Stream stream = response.Content.ReadAsStream();
					using var memory = new MemoryStream();
					stream.CopyTo(memory);
					return memory.ToArray();
				}
				catch (Exception exception) when (exception is IOException or HttpRequestException)
				{
					throw new BuildException(
						$"пакет '{package.Name}': не удалось прочитать ответ {uri}: {exception.Message}");
				}
			}
		}

		throw new BuildException(
			$"пакет '{package.Name}': больше {MaxRedirects} перенаправлений");
	}

	/// <summary>Распаковка с защитой от выхода за пределы каталога (zip slip).</summary>
	private static void Extract(byte[] archive, string destination)
	{
		Directory.CreateDirectory(destination);
		using var memory = new MemoryStream(archive, writable: false);
		using var zip = new ZipArchive(memory, ZipArchiveMode.Read);
		string root = Path.GetFullPath(destination);
		string prefix = root.EndsWith(Path.DirectorySeparatorChar)
			? root
			: root + Path.DirectorySeparatorChar;
		foreach (ZipArchiveEntry entry in zip.Entries)
		{
			if (entry.FullName.Length == 0)
				continue;
			string target = Path.GetFullPath(Path.Combine(root, entry.FullName));
			if (!string.Equals(target, root, StringComparison.OrdinalIgnoreCase) &&
				!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
				throw new BuildException($"пакет: недопустимый путь в архиве: {entry.FullName}");

			if (entry.Name.Length == 0)
			{
				Directory.CreateDirectory(target);
				continue;
			}

			Directory.CreateDirectory(Path.GetDirectoryName(target)!);
			entry.ExtractToFile(target, overwrite: true);
		}
	}

	/// <summary>Убрать единственную корневую папку архива (частый случай <c>project-1.0/</c>).</summary>
	private static void WriteCompleteMarker(string directory)
	{
		string[] entries = Directory.GetFileSystemEntries(directory);
		string content = entries.Length == 1 && Directory.Exists(entries[0])
			? Path.GetFileName(entries[0])
			: string.Empty;
		File.WriteAllText(Marker(directory), content);
	}

	private static string Marker(string directory) => Path.Combine(directory, ".complete");

	private static string ContentRoot(string directory)
	{
		string marker = Marker(directory);
		string relative = File.Exists(marker) ? File.ReadAllText(marker).Trim() : string.Empty;
		return relative.Length == 0
			? directory
			: Path.GetFullPath(Path.Combine(directory, relative));
	}

	private static void TryDelete(string directory)
	{
		try
		{
			if (Directory.Exists(directory))
				Directory.Delete(directory, recursive: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// Мусор в кэше не должен маскировать исходную ошибку.
		}
	}
}
