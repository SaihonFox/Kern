using Kern.Tests.Features.Support;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary><c>kern.compress</c> (spec round 6): gzip/deflate/brotli и файлы.</summary>
public sealed class CompressModuleTests
{
	[Fact]
	public void Gzip_and_deflate_round_trip()
	{
		const string source = """
			fun gzip_ok() -> bool {
				str text = "Kern Kern Kern Kern Kern"
				match compress.gunzip(compress.gzip(text)) {
					Ok(back) => back == text
					Err(_) => false
				}
			}

			fun deflate_ok() -> bool {
				str text = "привет, мир"
				match compress.inflate(compress.deflate(text)) {
					Ok(back) => back == text
					Err(_) => false
				}
			}

			fun brotli_ok() -> bool {
				match compress.unbrotli(compress.brotli("abc")) {
					Ok(back) => back == "abc"
					Err(_) => false
				}
			}
			""";

		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "gzip_ok"));
		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "deflate_ok"));
		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "brotli_ok"));
	}

	[Fact]
	public void Invalid_base64_returns_err()
	{
		const string source = """
			fun run() -> bool {
				match compress.gunzip("не base64") {
					Ok(_) => false
					Err(message) => message.len > 0
				}
			}
			""";

		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "run"));
	}

	[Fact]
	public void File_and_zip_helpers_round_trip()
	{
		string directory = Path.Combine(Path.GetTempPath(), "kern-compress-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		// Архив и распакованные файлы лежат ВНЕ упаковываемой папки: иначе
		// ZipFile.CreateFromDirectory пытается включить сам архив (на Windows файл
		// занят, на Linux получается лишняя запись — тест был платформозависимым).
		string inputDirectory = Path.Combine(directory, "input");
		Directory.CreateDirectory(inputDirectory);
		string input = Path.Combine(inputDirectory, "input.txt");
		string gzip = Path.Combine(directory, "input.txt.gz");
		string restored = Path.Combine(directory, "restored.txt");
		string archive = Path.Combine(directory, "pack.zip");
		string unpacked = Path.Combine(directory, "unpacked");
		File.WriteAllText(input, new string('k', 4096));
		string Slash(string value) => value.Replace('\\', '/');

		try
		{
			string source = $$"""
				fun run() -> i32 {
					i32 ok = 0
					match compress.gzip_file("{{Slash(input)}}", "{{Slash(gzip)}}") {
						Ok(_) => { ok += 1 }
						Err(_) => {}
					}
					match compress.gunzip_file("{{Slash(gzip)}}", "{{Slash(restored)}}") {
						Ok(_) => { ok += 1 }
						Err(_) => {}
					}
					match compress.zip_dir("{{Slash(inputDirectory)}}", "{{Slash(archive)}}") {
						Ok(_) => { ok += 1 }
						Err(_) => {}
					}
					match compress.zip_list("{{Slash(archive)}}") {
						Ok(names) => {
						ok += 1
						if names.contains("input.txt") { ok += 1 }
					}
						Err(_) => {}
					}
					match compress.unzip("{{Slash(archive)}}", "{{Slash(unpacked)}}") {
						Ok(_) => { ok += 1 }
						Err(_) => {}
					}
					ok
				}
				""";

			Assert.Equal(KValue.I32(6), StdPipeline.Run(source, "run"));
			Assert.Equal(File.ReadAllText(input), File.ReadAllText(restored));
			Assert.True(File.Exists(Path.Combine(unpacked, "input.txt")));
		}
		finally
		{
			if (Directory.Exists(directory))
				Directory.Delete(directory, recursive: true);
		}
	}
}
