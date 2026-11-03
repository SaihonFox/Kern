using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kern.Cli.Build;

/// <summary>
/// Манифест пакета <c>kern-package.json</c> и отделённая подпись <c>kern-package.sig</c>.
/// Подпись — RSA PKCS#1 SHA-256 по точным байтам манифеста; открытый ключ берётся из
/// <c>KERN_PACKAGE_PUBLIC_KEY</c> (PEM) или <c>KERN_PACKAGE_KEY_FILE</c>.
/// </summary>
internal sealed record PackageManifest(
	string Name,
	string Version,
	string Description,
	IReadOnlyList<string> Permissions)
{
	public const string FileName = "kern-package.json";
	public const string SignatureFileName = "kern-package.sig";

	private static readonly JsonSerializerOptions Options = new()
	{
		PropertyNameCaseInsensitive = true,
	};

	public static PackageManifest Read(
		string root,
		PackageModel package,
		string? trustedPublicKeyPem = null)
	{
		string manifestPath = Path.Combine(root, FileName);
		string signaturePath = Path.Combine(root, SignatureFileName);
		if (!File.Exists(manifestPath))
		{
			if (File.Exists(signaturePath))
				throw new BuildException(
					$"пакет '{package.Name}': есть {SignatureFileName} без {FileName}");
			return new PackageManifest(package.Name, "", "", []);
		}

		ManifestDto? dto;
		try
		{
			dto = JsonSerializer.Deserialize<ManifestDto>(File.ReadAllText(manifestPath), Options);
		}
		catch (JsonException exception)
		{
			throw new BuildException($"пакет '{package.Name}': некорректный {FileName}: {exception.Message}");
		}

		if (dto is null || string.IsNullOrWhiteSpace(dto.Name))
			throw new BuildException($"пакет '{package.Name}': в {FileName} нет имени");
		if (!string.Equals(dto.Name, package.Name, StringComparison.Ordinal))
			throw new BuildException(
				$"пакет '{package.Name}': манифест объявляет имя '{dto.Name}'");
		if (string.IsNullOrWhiteSpace(dto.Version))
			throw new BuildException($"пакет '{package.Name}': в {FileName} нет версии");

		if (!File.Exists(signaturePath))
			throw new BuildException(
				$"пакет '{package.Name}': {FileName} есть, но нет {SignatureFileName}");

		VerifySignature(package, manifestPath, signaturePath, trustedPublicKeyPem);
		return new PackageManifest(
			dto.Name,
			dto.Version,
			dto.Description ?? "",
			dto.Permissions ?? []);
	}

	private static void VerifySignature(
		PackageModel package,
		string manifestPath,
		string signaturePath,
		string? trustedPublicKeyPem)
	{
		string? pem = trustedPublicKeyPem;
		string? keyFile = Environment.GetEnvironmentVariable("KERN_PACKAGE_KEY_FILE");
		if (string.IsNullOrWhiteSpace(pem) && !string.IsNullOrWhiteSpace(keyFile))
		{
			try
			{
				pem = File.ReadAllText(keyFile);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				throw new BuildException($"пакет '{package.Name}': не удалось прочитать ключ '{keyFile}': {exception.Message}");
			}
		}

		if (string.IsNullOrWhiteSpace(pem))
			pem = Environment.GetEnvironmentVariable("KERN_PACKAGE_PUBLIC_KEY");
		if (string.IsNullOrWhiteSpace(pem))
			throw new BuildException(
				$"пакет '{package.Name}': для проверки подписи задайте KERN_PACKAGE_PUBLIC_KEY (PEM) " +
				"или KERN_PACKAGE_KEY_FILE");

		byte[] signature;
		try
		{
			signature = Convert.FromBase64String(File.ReadAllText(signaturePath).Trim());
		}
		catch (FormatException)
		{
			throw new BuildException($"пакет '{package.Name}': {SignatureFileName} не является Base64");
		}

		try
		{
			using RSA rsa = RSA.Create();
			rsa.ImportFromPem(pem);
			if (!rsa.VerifyData(
				File.ReadAllBytes(manifestPath), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
				throw new BuildException($"пакет '{package.Name}': подпись манифеста неверна");
		}
		catch (CryptographicException exception)
		{
			throw new BuildException($"пакет '{package.Name}': не удалось проверить подпись: {exception.Message}");
		}
	}

	public static string Sign(string manifestJson, string privateKeyPem)
	{
		using RSA rsa = RSA.Create();
		rsa.ImportFromPem(privateKeyPem);
		return Convert.ToBase64String(rsa.SignData(
			Encoding.UTF8.GetBytes(manifestJson), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
	}

	private sealed class ManifestDto
	{
		public string? Name { get; set; }

		public string? Version { get; set; }

		public string? Description { get; set; }

		public List<string>? Permissions { get; set; }
	}
}
