using Kern.Bytecode;
using Kern.Cli;
using Kern.Tests.Bytecode;
using Xunit;

namespace Kern.Tests.Cli;

/// <summary>
/// <c>kernc dis</c>: команда вызывается внутри процесса с перехваченными потоками,
/// поэтому тест не зависит от платформы и не запускает отдельный процесс.
/// </summary>
public sealed class DisCommandTests
{
	[Fact]
	public void Dis_prints_disassembler_listing()
	{
		string path = WriteTemp(BytecodeFixtures.MinimalBytes("dis_demo"));

		try
		{
			(int code, string output, string error) = Run("dis", path);

			Assert.Equal(0, code);
			Assert.Equal(string.Empty, error);
			Assert.Contains(".module \"dis_demo\"", output, StringComparison.Ordinal);
			Assert.Contains("PushVoid", output, StringComparison.Ordinal);
			Assert.Equal(Disassembler.Disassemble(KbcReader.Read(File.ReadAllBytes(path))), output);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void Dis_without_path_reports_usage()
	{
		(int code, string output, string error) = Run("dis");

		Assert.Equal(2, code);
		Assert.Equal(string.Empty, output);
		Assert.Contains("укажите файл .kbc", error);
	}

	[Fact]
	public void Dis_missing_file_reports_name()
	{
		string path = Path.Combine(Path.GetTempPath(), $"kern-cli-{Guid.NewGuid():N}.kbc");

		(int code, _, string error) = Run("dis", path);

		Assert.Equal(2, code);
		Assert.Contains("файл не найден", error);
		Assert.Contains(path, error);
	}

	[Fact]
	public void Dis_rejects_non_kbc_extension()
	{
		(int code, _, string error) = Run("dis", "program.kern");

		Assert.Equal(2, code);
		Assert.Contains("файл .kbc", error);
	}

	[Fact]
	public void Dis_reports_corrupted_module()
	{
		string path = WriteTemp([(byte)'N', (byte)'O', (byte)'P', (byte)'E', 0, 0]);

		try
		{
			(int code, string output, string error) = Run("dis", path);

			Assert.Equal(1, code);
			Assert.Equal(string.Empty, output);
			Assert.Contains("сигнатура", error);
		}
		finally
		{
			File.Delete(path);
		}
	}

	private static (int Code, string Output, string Error) Run(params string[] args)
	{
		using StringWriter stdout = new();
		using StringWriter stderr = new();
		int code = KernCli.Run(args, stdout, stderr);
		return (code, stdout.ToString(), stderr.ToString());
	}

	private static string WriteTemp(byte[] bytes)
	{
		string directory = Path.Combine(Path.GetTempPath(), "kern-cli-dis-tests");
		Directory.CreateDirectory(directory);
		string path = Path.Combine(directory, $"{Guid.NewGuid():N}.kbc");
		File.WriteAllBytes(path, bytes);
		return path;
	}
}
