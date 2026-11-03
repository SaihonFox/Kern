using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// M13: `kernw`/`kernw.cmd` скачивают закреплённый архив, проверяют SHA-256, кладут в
/// <c>KERN_HOME/dist</c> и передают аргументы <c>kernc</c> (docs/BUILDER.md §8).
/// </summary>
[Collection("timing")]
public sealed class KernwTests
{
	[Fact]
	public void Kernw_downloads_verifies_caches_and_forwards_arguments()
	{
		string root = NewDirectory("kernw-test");
		string home = Path.Combine(root, "home");
		try
		{
			CopyWrapper(root);
			byte[] archive = ReleaseArchive();
			string hash = Convert.ToHexString(SHA256.HashData(archive));
			string rid = Rid();
			WriteVersion(root, "9.9.9", rid, hash);

			using var server = new ZipServer(archive);
			ProcessResult first = RunWrapper(root, home, server.BaseUri);
			Assert.True(first.ExitCode == 0, first.Error);
			if (OperatingSystem.IsWindows())
				Assert.Contains("cmd.exe", first.Output, StringComparison.OrdinalIgnoreCase);
			else
				Assert.Contains("kernc cmd", first.Output);

			string executable = Path.Combine(
				home, "dist", "9.9.9", "kern-" + rid, OperatingSystem.IsWindows() ? "kernc.exe" : "kernc");
			Assert.True(File.Exists(executable), $"нет {executable}");

			// Второй запуск не должен ходить в сеть: сервер уже остановлен.
			string baseUri = server.BaseUri;
			server.Dispose();
			ProcessResult second = RunWrapper(root, home, baseUri);
			Assert.True(second.ExitCode == 0, second.Error);
			Assert.Equal(first.Output, second.Output);
		}
		finally
		{
			Delete(root);
		}
	}

	[Fact]
	public void Kernw_rejects_a_wrong_checksum()
	{
		string root = NewDirectory("kernw-bad-hash");
		try
		{
			CopyWrapper(root);
			byte[] archive = ReleaseArchive();
			string rid = Rid();
			WriteVersion(root, "9.9.9", rid, new string('0', 64));

			using var server = new ZipServer(archive);
			ProcessResult result = RunWrapper(root, Path.Combine(root, "home"), server.BaseUri);

			Assert.NotEqual(0, result.ExitCode);
			Assert.Contains("SHA-256", result.Error);
		}
		finally
		{
			Delete(root);
		}
	}

	private static ProcessResult RunWrapper(string root, string home, string baseUri)
	{
		var start = new ProcessStartInfo
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			WorkingDirectory = root,
		};
		if (OperatingSystem.IsWindows())
		{
			start.FileName = "cmd.exe";
			start.ArgumentList.Add("/c");
			start.ArgumentList.Add(Path.Combine(root, "kernw.cmd"));
		}
		else
		{
			start.FileName = "/bin/sh";
			start.ArgumentList.Add(Path.Combine(root, "kernw"));
		}
		start.ArgumentList.Add("cmd");
		start.Environment["KERN_HOME"] = home;
		start.Environment["KERNW_BASE_URL"] = baseUri;
		start.Environment["KERNW_ALLOW_HTTP"] = "1";

		using Process process = Process.Start(start)
			?? throw new InvalidOperationException("не удалось запустить kernw");
		string output = process.StandardOutput.ReadToEnd();
		string error = process.StandardError.ReadToEnd();
		process.WaitForExit();
		return new ProcessResult(process.ExitCode, output, error);
	}

	private static void CopyWrapper(string root)
	{
		string source = RepositoryRoot();
		Directory.CreateDirectory(Path.Combine(root, "tools"));
		File.Copy(Path.Combine(source, "kernw"), Path.Combine(root, "kernw"));
		File.Copy(Path.Combine(source, "kernw.cmd"), Path.Combine(root, "kernw.cmd"));
		File.Copy(
			Path.Combine(source, "tools", "kernw.ps1"),
			Path.Combine(root, "tools", "kernw.ps1"));
	}

	private static void WriteVersion(string root, string version, string rid, string sha256) =>
		File.WriteAllText(
			Path.Combine(root, "kern-version"),
			$"{version}\nsha256-{rid}={sha256}\n");

	/// <summary>Фальшивый релиз: <c>where.exe</c> на Windows и печатающий аргументы sh-скрипт.</summary>
	private static byte[] ReleaseArchive()
	{
		using var memory = new MemoryStream();
		using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
		{
			if (OperatingSystem.IsWindows())
			{
				string where = Path.Combine(
					Environment.GetFolderPath(Environment.SpecialFolder.System),
					"where.exe");
				using Stream target = archive.CreateEntry("kernc.exe").Open();
				using FileStream source = File.OpenRead(where);
				source.CopyTo(target);
			}
			else
			{
				using var writer = new StreamWriter(archive.CreateEntry("kernc").Open());
				writer.Write("#!/bin/sh\necho \"kernc $@\"\n");
			}
		}

		return memory.ToArray();
	}

	private static string Rid()
	{
		if (OperatingSystem.IsWindows())
			return "win-x64";
		if (OperatingSystem.IsMacOS())
			return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
		return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
	}

	private static string NewDirectory(string prefix)
	{
		string path = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(path);
		return path;
	}

	private static string RepositoryRoot()
	{
		string? current = AppContext.BaseDirectory;
		while (current is not null)
		{
			if (File.Exists(Path.Combine(current, "Kern.slnx")))
				return current;
			current = Path.GetDirectoryName(current);
		}

		throw new InvalidOperationException("не найден корень репозитория (Kern.slnx)");
	}

	private static void Delete(string directory)
	{
		try
		{
			if (Directory.Exists(directory))
				Directory.Delete(directory, recursive: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// Временный каталог мог остаться занятым.
		}
	}

	private sealed record ProcessResult(int ExitCode, string Output, string Error);

	/// <summary>Минимальный HTTP-сервер на TcpListener: отдаёт один zip на любой GET.</summary>
	private sealed class ZipServer : IDisposable
	{
		private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
		private readonly CancellationTokenSource _stop = new();
		private readonly Task _loop;
		private readonly byte[] _payload;

		public ZipServer(byte[] payload)
		{
			_payload = payload;
			_listener.Start();
			var endpoint = (IPEndPoint)_listener.LocalEndpoint;
			BaseUri = $"http://127.0.0.1:{endpoint.Port}";
			_loop = Task.Run(() => AcceptAsync(_stop.Token));
		}

		public string BaseUri { get; }

		private async Task AcceptAsync(CancellationToken token)
		{
			while (!token.IsCancellationRequested)
			{
				TcpClient client;
				try
				{
					client = await _listener.AcceptTcpClientAsync(token);
				}
				catch (OperationCanceledException)
				{
					return;
				}
				catch (SocketException)
				{
					return;
				}

				_ = Task.Run(() => ServeAsync(client, token), CancellationToken.None);
			}
		}

		private async Task ServeAsync(TcpClient client, CancellationToken token)
		{
			try
			{
				using (client)
				using (NetworkStream stream = client.GetStream())
				{
					await ReadHeadersAsync(stream, token);
					string headers =
						$"HTTP/1.1 200 OK\r\n" +
						$"Content-Length: {_payload.Length}\r\n" +
						"Content-Type: application/zip\r\n" +
						"Connection: close\r\n\r\n";
					await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), token);
					await stream.WriteAsync(_payload, token);
				}
			}
			catch (Exception exception) when (exception is IOException or OperationCanceledException)
			{
				// Клиент отключился или сервер остановлен — тест уже получил результат.
			}
		}

		private static async Task ReadHeadersAsync(NetworkStream stream, CancellationToken token)
		{
			var buffer = new byte[2048];
			var received = new List<byte>(buffer.Length);
			while (true)
			{
				int read = await stream.ReadAsync(buffer, token);
				if (read == 0)
					return;
				received.AddRange(buffer.AsSpan(0, read).ToArray());
				if (received.Count >= 4 &&
					received[^4] == '\r' && received[^3] == '\n' &&
					received[^2] == '\r' && received[^1] == '\n')
					return;
			}
		}

		public void Dispose()
		{
			if (_disposed)
				return;
			_disposed = true;
			_stop.Cancel();
			_listener.Stop();
			try
			{
				_loop.Wait(TimeSpan.FromSeconds(2));
			}
			catch (AggregateException)
			{
				// Остановка сервера — ожидаемая гонка.
			}
			_stop.Dispose();
		}

		private bool _disposed;
	}
}
