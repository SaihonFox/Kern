using System.IO.Compression;
using System.Text;
using Kern.Binding.Attributes;
using Kern.Binding.Values;
using Kern.Vm.Values;

namespace Kern.Runtime.Compression;

/// <summary>
/// <c>kern.compress</c> (spec round 6): бинарные данные ходят base64-строками.
/// <c>compress.gzip/deflate/brotli(text)</c> возвращают base64 сжатых байт,
/// <c>gunzip/inflate/unbrotli(b64)</c> — <c>result&lt;str, str&gt;</c>; файловые
/// операции тоже возвращают <c>result</c> с путём результата.
/// </summary>
[KernType("compress")]
public static class CompressApi
{
	private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

	private static string Pack(string text, Func<Stream, Stream> make)
	{
		using var buffer = new MemoryStream();
		using (Stream compressed = make(buffer))
		using (var writer = new StreamWriter(compressed, Utf8, bufferSize: 1024, leaveOpen: true))
			writer.Write(text);
		return Convert.ToBase64String(buffer.ToArray());
	}

	private static KResult<string, string> Unpack(string base64, Func<Stream, Stream> make)
	{
		try
		{
			using var input = new MemoryStream(Convert.FromBase64String(base64));
			using Stream decompressed = make(input);
			using var reader = new StreamReader(decompressed, Utf8);
			return KResult<string, string>.Success(reader.ReadToEnd());
		}
		catch (Exception error) when (error is FormatException or InvalidDataException or IOException)
		{
			return KResult<string, string>.Failure($"compress: не удалось распаковать: {error.Message}");
		}
	}

	/// <summary><c>gzip(text)</c>: сжать UTF-8 в gzip и вернуть base64.</summary>
	[KernStatic]
	public static string Gzip(string text) =>
		Pack(text, stream => new GZipStream(stream, CompressionLevel.Optimal, leaveOpen: true));

	/// <summary><c>gunzip(base64)</c>: распаковать gzip-строку.</summary>
	[KernStatic]
	public static KResult<string, string> Gunzip(string base64) =>
		Unpack(base64, stream => new GZipStream(stream, CompressionMode.Decompress));

	/// <summary><c>deflate(text)</c>: сжать в deflate и вернуть base64.</summary>
	[KernStatic]
	public static string Deflate(string text) =>
		Pack(text, stream => new DeflateStream(stream, CompressionLevel.Optimal, leaveOpen: true));

	/// <summary><c>inflate(base64)</c>: распаковать deflate-строку.</summary>
	[KernStatic]
	public static KResult<string, string> Inflate(string base64) =>
		Unpack(base64, stream => new DeflateStream(stream, CompressionMode.Decompress));

	/// <summary><c>brotli(text)</c>: сжать brotli и вернуть base64.</summary>
	[KernStatic]
	public static string Brotli(string text) =>
		Pack(text, stream => new BrotliStream(stream, CompressionLevel.Optimal, leaveOpen: true));

	/// <summary><c>unbrotli(base64)</c>: распаковать brotli-строку.</summary>
	[KernStatic]
	public static KResult<string, string> Unbrotli(string base64) =>
		Unpack(base64, stream => new BrotliStream(stream, CompressionMode.Decompress));

	/// <summary><c>gzip_file(path)</c>: сжать файл в <c>path.gz</c>, вернуть путь архива.</summary>
	[KernStatic]
	[KernName("gzip_file")]
	public static KResult<string, string> GzipFile(string path) => GzipFile(path, path + ".gz");

	/// <summary><c>gzip_file(path, output)</c>: сжать файл в указанный gzip-файл.</summary>
	[KernStatic]
	[KernName("gzip_file")]
	public static KResult<string, string> GzipFile(string path, string output)
	{
		try
		{
			using var source = File.OpenRead(path);
			using var destination = File.Create(output);
			using var compressed = new GZipStream(destination, CompressionLevel.Optimal);
			source.CopyTo(compressed);
			return KResult<string, string>.Success(output);
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			return KResult<string, string>.Failure($"compress: {error.Message}");
		}
	}

	/// <summary><c>gunzip_file(path, output)</c>: распаковать gzip-файл.</summary>
	[KernStatic]
	[KernName("gunzip_file")]
	public static KResult<string, string> GunzipFile(string path, string output)
	{
		try
		{
			using var source = File.OpenRead(path);
			using var compressed = new GZipStream(source, CompressionMode.Decompress);
			using var destination = File.Create(output);
			compressed.CopyTo(destination);
			return KResult<string, string>.Success(output);
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			return KResult<string, string>.Failure($"compress: {error.Message}");
		}
	}

	/// <summary><c>zip_dir(dir, archive)</c>: упаковать папку в zip.</summary>
	[KernStatic]
	[KernName("zip_dir")]
	public static KResult<string, string> ZipDir(string directory, string archive)
	{
		try
		{
			if (File.Exists(archive))
				File.Delete(archive);
			ZipFile.CreateFromDirectory(directory, archive);
			return KResult<string, string>.Success(archive);
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
		{
			return KResult<string, string>.Failure($"compress: {error.Message}");
		}
	}

	/// <summary><c>unzip(archive, dir)</c>: распаковать zip в папку (перезаписывая файлы).</summary>
	[KernStatic]
	public static KResult<string, string> Unzip(string archive, string directory)
	{
		try
		{
			ZipFile.ExtractToDirectory(archive, directory, overwriteFiles: true);
			return KResult<string, string>.Success(directory);
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			return KResult<string, string>.Failure($"compress: {error.Message}");
		}
	}

	/// <summary><c>zip_list(archive)</c>: имена записей архива.</summary>
	[KernStatic]
	[KernName("zip_list")]
	public static KResult<KList<string>, string> ZipList(string archive)
	{
		try
		{
			using var zip = ZipFile.OpenRead(archive);
			return KResult<KList<string>, string>.Success(
				new KList<string>(KValue.Obj(new KList(
					null,
					[.. zip.Entries.Select(entry => KValue.Str(entry.FullName))]))));
		}
		catch (Exception error) when (error is IOException or InvalidDataException)
		{
			return KResult<KList<string>, string>.Failure($"compress: {error.Message}");
		}
	}
}
