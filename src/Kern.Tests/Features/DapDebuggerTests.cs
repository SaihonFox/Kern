using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Kern.Cli;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// Удалённая отладка по DAP (<c>kernc debug --dap &lt;порт&gt;</c>): подключение по TCP,
/// точки останова, стек, вычисление выражения и продолжение.
/// </summary>
[Collection("timing")]
public sealed class DapDebuggerTests
{
	[Fact]
	public void Dap_client_stops_evaluates_and_continues()
	{
		const string source = """
			type Point(i32 x) {
				fun double() -> i32 x * 2
			}

			fun main {
				var point = Point(21)
				point.double()
			}
			""";

		int port = FreePort();
		string file = Path.Combine(Path.GetTempPath(), "kern-dap-" + Guid.NewGuid().ToString("N") + ".kern");
		File.WriteAllText(file, source);
		var stdout = new StringWriter();
		var stderr = new StringWriter();
		var exitCode = -1;
		var server = new Thread(() =>
		{
			exitCode = KernCli.Run(["debug", "--dap", port.ToString(), file], stdout, stderr);
		})
		{
			IsBackground = true,
		};
		server.Start();

		try
		{
			using var client = Connect(port);
			using var stream = client.GetStream();
			stream.ReadTimeout = 15_000;

			int initialize = Send(stream, "initialize", new JsonObject());
			JsonObject initialized = ReadUntil(stream, "response", initialize);
			Assert.True((bool?)initialized["success"]);

			int launch = Send(stream, "launch", new JsonObject { ["stopOnEntry"] = false });
			ReadUntil(stream, "event", 0, "initialized");

			int breakpoints = Send(stream, "setBreakpoints", new JsonObject
			{
				["source"] = new JsonObject { ["path"] = file },
				["breakpoints"] = new JsonArray(new JsonObject { ["line"] = 7 }),
			});
			Assert.True((bool?)ReadUntil(stream, "response", breakpoints)["success"]);

			int configuration = Send(stream, "configurationDone", new JsonObject());
			ReadUntil(stream, "response", configuration);

			JsonObject stopped = ReadUntil(stream, "event", 0, "stopped");
			Assert.Equal("breakpoint", (string?)stopped["body"]?["reason"]);

			int stack = Send(stream, "stackTrace", new JsonObject { ["threadId"] = 1 });
			JsonObject stackResponse = ReadUntil(stream, "response", stack);
			Assert.Equal("main", (string?)stackResponse["body"]?["stackFrames"]?[0]?["name"]);

			int evaluate = Send(stream, "evaluate", new JsonObject { ["expression"] = "point.x" });
			JsonObject evaluateResponse = ReadUntil(stream, "response", evaluate);
			Assert.Equal("21", (string?)evaluateResponse["body"]?["result"]);

			int continueRequest = Send(stream, "continue", new JsonObject { ["threadId"] = 1 });
			Assert.True((bool?)ReadUntil(stream, "response", continueRequest)["success"]);

			Assert.True(server.Join(TimeSpan.FromSeconds(20)), "программа должна завершиться");
			Assert.Equal(string.Empty, stderr.ToString());
			Assert.Equal(0, exitCode);
		}
		finally
		{
			File.Delete(file);
		}
	}

	private static int FreePort()
	{
		var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		int port = ((IPEndPoint)listener.LocalEndpoint).Port;
		listener.Stop();
		return port;
	}

	private static TcpClient Connect(int port)
	{
		Exception? last = null;
		for (var attempt = 0; attempt < 100; attempt++)
		{
			try
			{
				var client = new TcpClient();
				client.Connect(IPAddress.Loopback, port);
				return client;
			}
			catch (SocketException exception)
			{
				last = exception;
				Thread.Sleep(50);
			}
		}

		throw new TimeoutException($"DAP-сервер не поднялся на порту {port}", last);
	}

	private static int Send(NetworkStream stream, string command, JsonObject arguments)
	{
		int sequence = Interlocked.Increment(ref _sequence);
		var message = new JsonObject
		{
			["seq"] = sequence,
			["type"] = "request",
			["command"] = command,
			["arguments"] = arguments,
		};
		byte[] body = Encoding.UTF8.GetBytes(message.ToJsonString());
		byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
		stream.Write(header);
		stream.Write(body);
		stream.Flush();
		return sequence;
	}

	private static int _sequence;

	private static JsonObject ReadUntil(
		NetworkStream stream,
		string type,
		int requestSequence = 0,
		string? eventName = null)
	{
		while (true)
		{
			JsonObject message = Read(stream);
			if ((string?)message["type"] != type)
				continue;
			if (type == "response" && requestSequence != 0 && (int?)message["request_seq"] != requestSequence)
				continue;
			if (eventName is not null && (string?)message["event"] != eventName)
				continue;
			return message;
		}
	}

	private static JsonObject Read(NetworkStream stream)
	{
		int length = -1;
		while (true)
		{
			string? line = ReadLine(stream);
			if (line is null)
				throw new IOException("DAP: соединение закрыто");
			if (line.Length == 0)
				break;
			if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
				length = int.Parse(line["Content-Length:".Length..].Trim());
		}

		var body = new byte[length];
		var read = 0;
		while (read < length)
		{
			int chunk = stream.Read(body, read, length - read);
			if (chunk <= 0)
				throw new IOException("DAP: соединение закрыто");
			read += chunk;
		}

		return (JsonObject)JsonNode.Parse(body)!;
	}

	private static string? ReadLine(NetworkStream stream)
	{
		var builder = new StringBuilder();
		while (true)
		{
			int value = stream.ReadByte();
			if (value < 0)
				return null;
			if (value == '\n')
				return builder.ToString().TrimEnd('\r');
			builder.Append((char)value);
		}
	}
}
