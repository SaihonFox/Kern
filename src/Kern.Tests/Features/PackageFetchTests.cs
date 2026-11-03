using System.IO.Compression;
using System.Security.Cryptography;
using Kern.Binding.Resolution;
using Kern.Cli.Build;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// M13 (docs/BUILDER.md §8): минимальные удалённые пакеты <c>t.pkg</c> — HTTPS, обязательный
/// SHA-256, кэш <c>~/.kern/pkgs/&lt;sha256&gt;/</c>, подключение как include/проект или .kbc.
/// </summary>
public sealed class PackageFetchTests
{
	private const string PackageBuild = """
		fun build(workspace w) {
			w.target("lib", fun(target t) {
				t.kind = "lib"
				t.src = ["src"]
			})
		}
		""";

	private const string PackageSource = """
		pub fun double(i32 value) -> i32 value * 2
		""";

	private const string ConsumerMain = """
		use lib.*
		fun main() -> i32 double(21)
		""";

	[Fact]
	public void Fetch_verifies_hash_extracts_and_reuses_cache()
	{
		string cache = NewCacheRoot();
		try
		{
			byte[] archive = Zip(
				("mathx/build.kern", PackageBuild),
				("mathx/src/lib.kern", PackageSource));
			string hash = Sha256(archive);
			var fetcher = new PackageFetcher(
				new FakeHandler(_ => Bytes(archive)),
				cache);

			string root = fetcher.Fetch(Package("mathx", "https://example.test/mathx.zip", hash));

			Assert.True(File.Exists(Path.Combine(root, "build.kern")));
			Assert.False(File.Exists(Path.Combine(root, "mathx", "build.kern")));
			Assert.True(File.Exists(Path.Combine(cache, hash, ".complete")));

			// Повторное подключение не ходит в сеть: обработчик запрещает запросы.
			var cached = new PackageFetcher(
				new FakeHandler(_ => throw new InvalidOperationException("сеть не ожидалась")),
				cache);
			Assert.Equal(root, cached.Fetch(Package("mathx", "https://example.test/mathx.zip", hash)));
		}
		finally
		{
			Delete(cache);
		}
	}

	[Fact]
	public void Fetch_verifies_manifest_signature_and_rejects_bad_ones()
	{
		string cache = NewCacheRoot();
		try
		{
			const string manifest = "{\"name\":\"mathx\",\"version\":\"1.0.0\",\"permissions\":[\"fs.read\"]}";
			using RSA key = RSA.Create(2048);
			string privatePem = key.ExportPkcs8PrivateKeyPem();
			string publicPem = key.ExportSubjectPublicKeyInfoPem();
			string signature = PackageManifest.Sign(manifest, privatePem);
			byte[] archive = Zip(
				("mathx/kern-package.json", manifest),
				("mathx/kern-package.sig", signature),
				("mathx/build.kern", PackageBuild),
				("mathx/src/lib.kern", PackageSource));
			string hash = Sha256(archive);
			var fetcher = new PackageFetcher(
				new FakeHandler(_ => Bytes(archive)),
				cache,
				publicPem);

			string root = fetcher.Fetch(Package("mathx", "https://example.test/mathx.zip", hash));
			Assert.True(File.Exists(Path.Combine(root, "build.kern")));

			using RSA other = RSA.Create(2048);
			byte[] badSignature = Zip(
				("mathx/kern-package.json", manifest),
				("mathx/kern-package.sig", PackageManifest.Sign(manifest, other.ExportPkcs8PrivateKeyPem())),
				("mathx/build.kern", PackageBuild));
			var badFetcher = new PackageFetcher(
				new FakeHandler(_ => Bytes(badSignature)),
				cache,
				publicPem);
			var bad = Assert.Throws<BuildException>(() =>
				badFetcher.Fetch(Package("mathx", "https://example.test/bad.zip", Sha256(badSignature))));
			Assert.Contains("подпись", bad.Message);

			byte[] noSignature = Zip(
				("mathx/kern-package.json", manifest),
				("mathx/build.kern", PackageBuild));
			var noSignatureFetcher = new PackageFetcher(
				new FakeHandler(_ => Bytes(noSignature)),
				cache,
				publicPem);
			var missing = Assert.Throws<BuildException>(() =>
				noSignatureFetcher.Fetch(Package("mathx", "https://example.test/nosig.zip", Sha256(noSignature))));
			Assert.Contains("kern-package.sig", missing.Message);
		}
		finally
		{
			Delete(cache);
		}
	}

	[Fact]
	public void Fetch_rejects_http_and_wrong_hash_and_zip_slip()
	{
		string cache = NewCacheRoot();
		try
		{
			byte[] archive = Zip(("build.kern", PackageBuild));
			string hash = Sha256(archive);
			var fetcher = new PackageFetcher(new FakeHandler(_ => Bytes(archive)), cache);

			var insecure = Assert.Throws<BuildException>(() =>
				fetcher.Fetch(Package("bad", "http://example.test/bad.zip", hash)));
			Assert.Contains("https", insecure.Message);

			var mismatch = Assert.Throws<BuildException>(() =>
				fetcher.Fetch(Package("bad", "https://example.test/bad.zip", new string('0', 64))));
			Assert.Contains("SHA-256", mismatch.Message);

			byte[] slip = Zip(("../escape.txt", "x"), ("build.kern", PackageBuild));
			string slipHash = Sha256(slip);
			var slipFetcher = new PackageFetcher(new FakeHandler(_ => Bytes(slip)), cache);
			var error = Assert.Throws<BuildException>(() =>
				slipFetcher.Fetch(Package("slip", "https://example.test/slip.zip", slipHash)));
			Assert.Contains("недопустимый путь", error.Message);
		}
		finally
		{
			Delete(cache);
		}
	}

	[Fact]
	public void Package_project_becomes_an_include_and_compiles()
	{
		string cache = NewCacheRoot();
		string consumer = Path.Combine(Path.GetTempPath(), "kern-pkg-consumer-" + Guid.NewGuid().ToString("N"));
		try
		{
			byte[] archive = Zip(
				("mathx/build.kern", PackageBuild),
				("mathx/src/lib.kern", PackageSource));
			string hash = Sha256(archive);
			var fetcher = new PackageFetcher(new FakeHandler(_ => Bytes(archive)), cache);
			Directory.CreateDirectory(Path.Combine(consumer, "src"));
			File.WriteAllText(
				Path.Combine(consumer, "build.kern"),
				$$"""
				fun build(workspace w) {
					w.target("app", fun(target t) {
						t.deps = ["mathx:lib"]
						t.pkg("mathx", url: "https://example.test/mathx.zip", sha256: "{{hash}}")
					})
				}
				""");
			File.WriteAllText(Path.Combine(consumer, "src", "main.kern"), ConsumerMain);

			BuildSession session = BuildSession.Open(consumer, fetcher);
			BuildTargetResult built = BuildTargetCompiler.CompileApp(session);

			Assert.True(
				built.Compile.Success,
				string.Join('\n', built.Compile.ProjectMessages));
			var program = built.Compile.Program!;
			var interpreter = new Interpreter(program.Module, new NativeResolver(program.Catalog));
			KValue value = interpreter.Run(program.Module.EntryPoint, []);
			Assert.Equal(KValue.I32(42), value);
		}
		finally
		{
			Delete(cache);
			Delete(consumer);
		}
	}

	[Fact]
	public void Package_kbc_is_added_to_kbc_paths()
	{
		string cache = NewCacheRoot();
		try
		{
			byte[] archive = Zip(("out/bundle.kbc", "not-a-real-kbc"));
			string hash = Sha256(archive);
			var fetcher = new PackageFetcher(new FakeHandler(_ => Bytes(archive)), cache);
			var target = new TargetModel
			{
				Name = "app",
				Packages = [Package("bundle", "https://example.test/bundle.zip", hash)],
			};
			var workspace = new BuildModel
			{
				Root = cache,
				Name = "consumer",
				Compiler = CompilerSettingsModel.Default,
				Targets = [target],
			};

			PackageResolver.Result result = PackageResolver.Resolve(
				workspace,
				fetcher,
				_ => throw new InvalidOperationException("проект не ожидался"));

			string path = Assert.Single(result.Workspace.Targets[0].KbcPaths);
			Assert.Equal("bundle.kbc", Path.GetFileName(path));
			Assert.Empty(result.Projects);
		}
		finally
		{
			Delete(cache);
		}
	}

	private static PackageModel Package(string name, string url, string sha256) => new()
	{
		Name = name,
		Url = url,
		Sha256 = sha256,
	};

	private static string NewCacheRoot() =>
		Path.Combine(Path.GetTempPath(), "kern-pkg-tests-" + Guid.NewGuid().ToString("N"));

	private static byte[] Zip(params (string Path, string Text)[] files)
	{
		using var memory = new MemoryStream();
		using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
		{
			foreach (var (path, text) in files)
			{
				ZipArchiveEntry entry = archive.CreateEntry(path);
				using var writer = new StreamWriter(entry.Open());
				writer.Write(text);
			}
		}

		return memory.ToArray();
	}

	private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

	private static HttpResponseMessage Bytes(byte[] bytes) => new(System.Net.HttpStatusCode.OK)
	{
		Content = new ByteArrayContent(bytes),
	};

	private static void Delete(string directory)
	{
		try
		{
			if (Directory.Exists(directory))
				Directory.Delete(directory, recursive: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// Временный каталог мог остаться занятым — тест уже завершил проверки.
		}
	}

	private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
	{
		protected override HttpResponseMessage Send(
			HttpRequestMessage request,
			CancellationToken cancellationToken) => respond(request);

		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken) => Task.FromResult(respond(request));
	}
}
