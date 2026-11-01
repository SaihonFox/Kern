using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Kern.Binding.Catalog;
using Kern.Binding.Resolution;
using Kern.Compiler.CodeGen;
using Kern.Lib.Backend.Routing;
using Kern.Lib.Di;
using Kern.Tests.Features.Support;
using Kern.Vm.Execution;

namespace Kern.Tests.Features.Backend;

/// <summary>
/// Поднимает настоящий <c>app</c> на loopback-порту в отдельном потоке: компилирует
/// Kern-исходник, создаёт VM в этом потоке (владелец планировщика) и вызывает <c>main</c>.
/// </summary>
internal sealed class BackendTestServer : IDisposable
{
	private readonly Thread _thread;
	private volatile Exception? _error;
	private bool _disposed;

	private BackendTestServer(int port, Thread thread)
	{
		Port = port;
		_thread = thread;
	}

	public int Port { get; }

	public string BaseUrl => $"http://localhost:{Port}";

	public static BackendTestServer Start(Func<int, string> sourceFactory)
	{
		int port = FreePort();
		string source = sourceFactory(port);
		NativeCatalog catalog = StdPipeline.Catalog(typeof(KernApp).Assembly, typeof(KernContainer).Assembly);
		CodegenResult program = StdPipeline.Compile(source, catalog);

		BackendTestServer? server = null;
		var thread = new Thread(() =>
		{
			try
			{
				var interpreter = new Interpreter(program.Module, new NativeResolver(catalog));
				_ = interpreter.Run("main");
			}
			catch (Exception exception)
			{
				server!._error = exception;
			}
		})
		{
			IsBackground = true,
			Name = $"kern-backend-test-{port}",
		};

		server = new BackendTestServer(port, thread);
		thread.Start();
		server.WaitUntilReady();
		return server;
	}

	public HttpClient CreateClient(bool cookies = false)
	{
		var handler = new HttpClientHandler { UseCookies = cookies };
		if (cookies)
			handler.CookieContainer = new CookieContainer();
		return new HttpClient(handler)
		{
			BaseAddress = new Uri(BaseUrl),
			Timeout = TimeSpan.FromSeconds(10),
		};
	}

	private void WaitUntilReady()
	{
		using HttpClient client = CreateClient();
		var watch = Stopwatch.StartNew();
		while (watch.Elapsed < TimeSpan.FromSeconds(10))
		{
			if (_error is { } error)
				throw new InvalidOperationException("backend-сервер упал при запуске", error);
			try
			{
				using HttpResponseMessage response = client.GetAsync("/health").GetAwaiter().GetResult();
				if (response.IsSuccessStatusCode)
					return;
			}
			catch (HttpRequestException)
			{
				// Порт ещё не слушается.
			}

			Thread.Sleep(20);
		}

		if (_error is { } late)
			throw new InvalidOperationException("backend-сервер упал при запуске", late);
		throw new TimeoutException($"backend-сервер не ответил на {BaseUrl}/health");
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		try
		{
			using HttpClient client = CreateClient();
			using HttpResponseMessage response = client.GetAsync("/stop").GetAwaiter().GetResult();
		}
		catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
		{
			// Сервер мог уже остановиться сам.
		}

		if (!_thread.Join(TimeSpan.FromSeconds(10)))
			throw new TimeoutException($"backend-тест-сервер {BaseUrl} не завершился после /stop");
		if (_error is { } error && error is not OperationCanceledException)
			throw new InvalidOperationException("backend-сервер завершился с ошибкой", error);
	}

	private static int FreePort()
	{
		var probe = new TcpListener(IPAddress.Loopback, 0);
		probe.Start();
		int port = ((IPEndPoint)probe.LocalEndpoint).Port;
		probe.Stop();
		return port;
	}
}
