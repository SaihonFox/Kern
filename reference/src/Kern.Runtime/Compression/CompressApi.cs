using System.IO.Compression;
using System.Text;
using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;

namespace Kern.Runtime.Compression;

/// <summary>
/// Сжатие. Бинарные данные в Kern передаются base64-строками: <c>compress.gzip("текст")</c> → base64 сжатых байт,
/// <c>compress.gunzip(b64)</c> → <c>result&lt;str, str&gt;</c>. Для файлов — <c>gzip_file</c>/<c>gunzip_file</c>, <c>zip_dir</c>/<c>unzip</c>.
/// </summary>
[KernType("compress")]
public static class CompressApi
{

	private static string Pack(string text, Func<Stream, Stream> make)
	{
		using var ms = new MemoryStream();
		using (var z = make(ms)) z.Write(Encoding.UTF8.GetBytes(text));
		return Convert.ToBase64String(ms.ToArray());
	}

	private static KResult<string, string> Unpack(string b64, Func<Stream, Stream> make)
	{
		try
		{
			using var input = new MemoryStream(Convert.FromBase64String(b64));
			using var z = make(input);
			using var reader = new StreamReader(z, Encoding.UTF8);
			return Kv.Ok<string>(Kern.Vm.Values.KValue.Str(reader.ReadToEnd()));
		}
		catch (Exception e) when (e is FormatException or InvalidDataException or IOException)
		{
			return Kv.Err<string>("не удалось распаковать: " + e.Message);
		}
	}

	[KernStatic] public static string Gzip(string text) => Pack(text, s => new GZipStream(s, CompressionLevel.Optimal, true));
	[KernStatic] public static KResult<string, string> Gunzip(string b64) => Unpack(b64, s => new GZipStream(s, CompressionMode.Decompress));
	[KernStatic] public static string Deflate(string text) => Pack(text, s => new DeflateStream(s, CompressionLevel.Optimal, true));
	[KernStatic] public static KResult<string, string> Inflate(string b64) => Unpack(b64, s => new DeflateStream(s, CompressionMode.Decompress));
	[KernStatic] public static string Brotli(string text) => Pack(text, s => new BrotliStream(s, CompressionLevel.Optimal, true));
	[KernStatic] public static KResult<string, string> Unbrotli(string b64) => Unpack(b64, s => new BrotliStream(s, CompressionMode.Decompress));

	/// <summary>Сжать файл в gzip; возвращает путь результата (по умолчанию path + ".gz").</summary>
	[KernStatic] public static KResult<string, string> GzipFile(string path) => GzipFile(path, path + ".gz");

	[KernStatic] public static KResult<string, string> GzipFile(string path, string output)
	{
		try
		{
			using var src = File.OpenRead(path);
			using var dst = File.Create(output);
			using var z = new GZipStream(dst, CompressionLevel.Optimal);
			src.CopyTo(z);
			return Kv.Ok<string>(Kern.Vm.Values.KValue.Str(output));
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Kv.Err<string>(e.Message); }
	}

	[KernStatic] public static KResult<string, string> GunzipFile(string path, string output)
	{
		try
		{
			using var src = File.OpenRead(path);
			using var z = new GZipStream(src, CompressionMode.Decompress);
			using var dst = File.Create(output);
			z.CopyTo(dst);
			return Kv.Ok<string>(Kern.Vm.Values.KValue.Str(output));
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return Kv.Err<string>(e.Message); }
	}

	/// <summary>Упаковать папку в zip-архив.</summary>
	[KernStatic] public static KResult<string, string> ZipDir(string dir, string archive)
	{
		try
		{
			if (File.Exists(archive)) File.Delete(archive);
			ZipFile.CreateFromDirectory(dir, archive);
			return Kv.Ok<string>(Kern.Vm.Values.KValue.Str(archive));
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { return Kv.Err<string>(e.Message); }
	}

	/// <summary>Распаковать zip-архив в папку.</summary>
	[KernStatic] public static KResult<string, string> Unzip(string archive, string dir)
	{
		try
		{
			ZipFile.ExtractToDirectory(archive, dir, overwriteFiles: true);
			return Kv.Ok<string>(Kern.Vm.Values.KValue.Str(dir));
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return Kv.Err<string>(e.Message); }
	}

	/// <summary>Имена записей zip-архива.</summary>
	[KernStatic] public static KResult<KList<string>, string> ZipList(string archive)
	{
		try
		{
			using var z = ZipFile.OpenRead(archive);
			return KResult<KList<string>, string>.Ok(Kern.Vm.Values.KValue.Obj(Kv.Strs(z.Entries.Select(e => e.FullName)).Inner));
		}
		catch (Exception e) when (e is IOException or InvalidDataException) { return KResult<KList<string>, string>.Err(Kern.Vm.Values.KValue.Str(e.Message)); }
	}
}
