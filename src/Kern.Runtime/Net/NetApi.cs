using System.Net;
using System.Net.Sockets;
using Kern.Binding.Attributes;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Net;

/// <summary>
/// <c>kern.net</c> (spec §16): имя хоста, разрешение имён и проверка TCP-доступности.
/// Полноценный сокет-клиент не входит в этот модуль; HTTP — в <c>kern.http</c>.
/// </summary>
[KernModule("kern.net")]
public static class NetApi
{
	/// <summary><c>net.hostname()</c>: имя текущего хоста.</summary>
	[KernMember]
	public static string Hostname() => Dns.GetHostName();

	/// <summary><c>net.resolve(host)</c>: IP-адреса хоста (IPv4 и IPv6).</summary>
	[KernMember]
	[KernReturn("list<str>")]
	public static KValue Resolve(string host)
	{
		try
		{
			return Names(Dns.GetHostAddresses(host));
		}
		catch (Exception error) when (error is SocketException or ArgumentException)
		{
			throw new KernPanic($"net.resolve: не удалось разрешить '{host}': {error.Message}");
		}
	}

	/// <summary><c>net.ip_of(host)</c>: первый адрес или <c>nil</c>.</summary>
	[KernMember]
	[KernName("ip_of")]
	[KernReturn("str?")]
	public static string? IpOf(string host)
	{
		try
		{
			return Dns.GetHostAddresses(host).FirstOrDefault()?.ToString();
		}
		catch (Exception error) when (error is SocketException or ArgumentException)
		{
			return null;
		}
	}

	/// <summary><c>net.local_addresses()</c>: адреса текущей машины.</summary>
	[KernMember]
	[KernName("local_addresses")]
	[KernReturn("list<str>")]
	public static KValue LocalAddresses() => Names(Dns.GetHostAddresses(Dns.GetHostName()));

	/// <summary><c>net.is_reachable(host, port, timeout_ms)</c>: TCP-соединение за указанное время.</summary>
	[KernMember]
	[KernName("is_reachable")]
	public static bool IsReachable(string host, int port, int timeoutMs)
	{
		if (port is < 1 or > 65535)
			throw new KernPanic($"net.is_reachable: недопустимый порт {port}");
		try
		{
			using var client = new TcpClient();
			using var cancellation = new CancellationTokenSource(System.Math.Max(1, timeoutMs));
			client.ConnectAsync(host, port, cancellation.Token).AsTask().GetAwaiter().GetResult();
			return true;
		}
		catch (Exception error) when (error is SocketException or OperationCanceledException or ArgumentException)
		{
			return false;
		}
	}

	private static KValue Names(IEnumerable<IPAddress> addresses) =>
		KValue.Obj(new KList(null, addresses.Select(address => KValue.Str(address.ToString())).Distinct()));
}
