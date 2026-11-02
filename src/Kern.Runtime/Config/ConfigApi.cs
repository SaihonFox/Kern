using System.Globalization;
using System.Text;
using Kern.Binding.Attributes;
using Kern.Binding.Values;
using Kern.Runtime.Json;
using Kern.Runtime.Yaml;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Config;

/// <summary>
/// <c>config</c> (spec §16, раунд 27): плоский набор ключей. Вложенные объекты JSON/YAML
/// разворачиваются в точечные ключи (<c>db.host</c>); массивы скаляров сохраняются списком.
/// </summary>
[KernType("config")]
public sealed class KernConfig
{
	internal Dictionary<string, object> Values { get; } = new(StringComparer.Ordinal);

	internal List<string> Order { get; } = [];

	internal void Put(string key, object value)
	{
		if (!Values.ContainsKey(key))
			Order.Add(key);
		Values[key] = value;
	}

	internal void PutScalar(string key, string value) => Put(key, value);

	internal void PutList(string key, IEnumerable<string> values) => Put(key, new List<string>(values));

	internal string? Scalar(string key)
	{
		if (!Values.TryGetValue(key, out object? value))
			return null;
		return value switch
		{
			string text => text,
			List<string> list => string.Join(",", list),
			_ => null,
		};
	}

	internal List<string>? ListOf(string key) => Values.TryGetValue(key, out object? value)
		? value as List<string>
		: null;
}

/// <summary>Члены <c>config</c>; первый параметр — <c>self</c>.</summary>
[KernType("config")]
public static class ConfigMembers
{
	/// <summary><c>c.get(key) -&gt; str?</c>: значение или <c>nil</c>.</summary>
	[KernMember]
	public static string? Get(KNativeBox self, string key) => Require(self).Scalar(key);

	/// <summary><c>c.get_or(key, fallback) -&gt; str</c>.</summary>
	[KernName("get_or")]
	[KernMember]
	public static string GetOr(KNativeBox self, string key, string fallback) =>
		Require(self).Scalar(key) ?? fallback;

	/// <summary><c>c.get_i64(key) -&gt; i64?</c>: <c>nil</c>, если ключа нет или он не число.</summary>
	[KernName("get_i64")]
	[KernMember]
	public static long? GetI64(KNativeBox self, string key) =>
		long.TryParse(Require(self).Scalar(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
			? value
			: null;

	/// <summary><c>c.get_f64(key) -&gt; f64?</c>.</summary>
	[KernName("get_f64")]
	[KernMember]
	public static double? GetF64(KNativeBox self, string key) =>
		double.TryParse(Require(self).Scalar(key), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
			? value
			: null;

	/// <summary><c>c.get_bool(key) -&gt; bool?</c>: true/false, 1/0, yes/no, on/off.</summary>
	[KernName("get_bool")]
	[KernMember]
	public static bool? GetBool(KNativeBox self, string key) =>
		Require(self).Scalar(key)?.Trim().ToLowerInvariant() switch
		{
			"true" or "1" or "yes" or "on" => true,
			"false" or "0" or "no" or "off" => false,
			_ => null,
		};

	/// <summary><c>c.get_list(key) &gt; list&lt;str&gt;</c>: список, строка через запятую или один элемент.</summary>
	[KernName("get_list")]
	[KernMember]
	public static KList<string> GetList(KNativeBox self, string key)
	{
		KernConfig config = Require(self);
		List<string>? list = config.ListOf(key);
		if (list is not null)
			return Strings(list);
		string? scalar = config.Scalar(key);
		if (string.IsNullOrEmpty(scalar))
			return Strings([]);
		return Strings(scalar.Split(',').Select(item => item.Trim()));
	}

	/// <summary><c>c.has(key)</c>: есть ли ключ.</summary>
	[KernMember]
	public static bool Has(KNativeBox self, string key) => Require(self).Values.ContainsKey(key);

	/// <summary><c>c.keys() &gt; list&lt;str&gt;</c>: ключи в порядке добавления.</summary>
	[KernMember]
	public static KList<string> Keys(KNativeBox self) => Strings(Require(self).Order);

	/// <summary><c>c.set(key, value)</c>: записать скалярное значение.</summary>
	[KernMember]
	public static void Set(KNativeBox self, string key, string value) =>
		Require(self).PutScalar(key, value);

	/// <summary><c>c.merge(other)</c>: значения второго конфига важнее.</summary>
	[KernMember]
	public static void Merge(KNativeBox self, KernConfig other)
	{
		KernConfig target = Require(self);
		foreach (string key in other.Order)
		{
			object value = other.Values[key];
			if (value is List<string> list)
				target.PutList(key, list);
			else
				target.PutScalar(key, (string)value);
		}
	}

	/// <summary><c>c.to_str()</c>: строки <c>key=value</c> в порядке добавления.</summary>
	[KernMember]
	public static string ToStr(KNativeBox self)
	{
		var builder = new StringBuilder();
		KernConfig config = Require(self);
		foreach (string key in config.Order)
		{
			if (builder.Length > 0)
				builder.Append('\n');
			builder.Append(key).Append('=').Append(config.Scalar(key));
		}

		return builder.ToString();
	}

	internal static KList<string> Strings(IEnumerable<string> items) =>
		new(KValue.Obj(new Kern.Vm.Values.KList(null, [.. items.Select(KValue.Str)])));

	private static KernConfig Require(KNativeBox self) => self.Value switch
	{
		KernConfig config => config,
		_ => throw new KernPanic($"config: ожидался конфиг, получено {self.Value.GetType().Name}"),
	};
}

/// <summary>Модуль <c>kern.config</c>: загрузка и разбор (spec §16).</summary>
[KernModule("kern.config")]
public static class ConfigApi
{
	private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

	/// <summary><c>config.empty()</c>: пустой конфиг.</summary>
	[KernMember]
	public static KernConfig Empty() => new();

	/// <summary><c>config.load(path) -&gt; result&lt;config, str&gt;</c>: формат по расширению.</summary>
	[KernMember]
	public static KResult<KernConfig, string> Load(string path, Interpreter interpreter)
	{
		try
		{
			string text = System.IO.File.ReadAllText(path, Utf8);
			return ParseText(text, FormatOf(path), interpreter);
		}
		catch (Exception error) when (error is IOException
			or UnauthorizedAccessException
			or ArgumentException
			or NotSupportedException)
		{
			return KResult<KernConfig, string>.Failure(
				$"config: не удалось прочитать '{path}': {error.Message}");
		}
	}

	/// <summary><c>config.from_str(text, format) -&gt; result&lt;config, str&gt;</c>.</summary>
	[KernName("from_str")]
	[KernMember]
	public static KResult<KernConfig, string> FromStr(string text, string format, Interpreter interpreter) =>
		ParseText(text, format, interpreter);

	private static KResult<KernConfig, string> ParseText(string text, string format, Interpreter interpreter)
	{
		switch (Normalize(format))
		{
			case "json":
				KResult<KValue, string> json = JsonApi.Parse(text, interpreter);
				return json.IsOk
					? FlattenDocument(json.Ok)
					: KResult<KernConfig, string>.Failure(json.Err);
			case "yaml":
				KResult<KValue, string> yaml = YamlApi.Parse(text, interpreter);
				return yaml.IsOk
					? FlattenDocument(yaml.Ok)
					: KResult<KernConfig, string>.Failure(yaml.Err);
			case "env":
				return KResult<KernConfig, string>.Success(ParsePairs(text, stripExport: true));
			case "kv":
				return KResult<KernConfig, string>.Success(ParsePairs(text, stripExport: false));
			default:
				return KResult<KernConfig, string>.Failure($"config: неизвестный формат '{format}'");
		}
	}

	private static KResult<KernConfig, string> FlattenDocument(KValue value)
	{
		var config = new KernConfig();
		if (value.IsNilOrNone)
			return KResult<KernConfig, string>.Success(config);
		if (value.Ref is not Kern.Vm.Values.KDict)
			return KResult<KernConfig, string>.Failure("config: ожидался объект верхнего уровня");
		Flatten(value, string.Empty, config);
		return KResult<KernConfig, string>.Success(config);
	}

	private static void Flatten(KValue value, string prefix, KernConfig config)
	{
		switch (value.Ref)
		{
			case Kern.Vm.Values.KDict dict:
				foreach (var pair in dict.Items)
				{
					string key = KeyText(pair.Key);
					string path = prefix.Length == 0 ? key : prefix + "." + key;
					Flatten(pair.Value, path, config);
				}

				return;
			case Kern.Vm.Values.KList list when prefix.Length > 0:
				config.PutList(prefix, list.Items.Select(ScalarText));
				return;
			case Kern.Vm.Values.KList:
				return;
			default:
				if (prefix.Length > 0)
					config.PutScalar(prefix, ScalarText(value));
				return;
		}
	}

	private static KernConfig ParsePairs(string text, bool stripExport)
	{
		var config = new KernConfig();
		foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
		{
			string line = raw.Trim();
			if (line.Length == 0 || line.StartsWith('#'))
				continue;
			if (stripExport && line.StartsWith("export ", StringComparison.Ordinal))
				line = line["export ".Length..].TrimStart();
			int equals = line.IndexOf('=');
			if (equals <= 0)
				continue;
			string key = line[..equals].Trim();
			string value = Unquote(line[(equals + 1)..].Trim());
			if (key.Length > 0)
				config.PutScalar(key, value);
		}

		return config;
	}

	private static string Unquote(string value)
	{
		if (value.Length >= 2 &&
			((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
			return value[1..^1];
		return value;
	}

	private static string ScalarText(KValue value) => value.Kind switch
	{
		ValueKind.None or ValueKind.Nil => "null",
		ValueKind.Bool => value.AsBool ? "true" : "false",
		ValueKind.Char => value.AsChar.ToString(),
		ValueKind.Str => value.AsStr,
		ValueKind.I8 or ValueKind.I16 or ValueKind.I32 or ValueKind.I64 =>
			value.AsI64.ToString(CultureInfo.InvariantCulture),
		ValueKind.U8 or ValueKind.U16 or ValueKind.U32 or ValueKind.U64 =>
			value.AsU64.ToString(CultureInfo.InvariantCulture),
		ValueKind.F16 or ValueKind.F32 or ValueKind.F64 =>
			value.AsF64.ToString("R", CultureInfo.InvariantCulture),
		_ => JsonApi.Serialize(value),
	};

	private static string KeyText(KValue key) => key.Kind switch
	{
		ValueKind.Str => key.AsStr,
		ValueKind.Char => key.AsChar.ToString(),
		_ => ScalarText(key),
	};

	private static string FormatOf(string path)
	{
		string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
		return extension switch
		{
			".json" => "json",
			".yaml" or ".yml" => "yaml",
			".env" => "env",
			_ => "kv",
		};
	}

	private static string Normalize(string format) => format.Trim().TrimStart('.').ToLowerInvariant() switch
	{
		"json" => "json",
		"yaml" or "yml" => "yaml",
		"env" => "env",
		"kv" or "key=value" or "key-value" or "properties" or "props" or "" => "kv",
		_ => format.Trim().ToLowerInvariant(),
	};
}
