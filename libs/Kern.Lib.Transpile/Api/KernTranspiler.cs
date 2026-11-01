using Kern.Binding.Attributes;
using Kern.Binding.Values;
using Kern.Lib.Transpile.Core;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Lib.Transpile.Api;

/// <summary>
/// Транспилятор Kern → C#/Kotlin. В Kern:
/// <code>
/// use kern.transpile.*
/// var t = transpiler("csharp")
/// t.map_script("mappings.kern")
/// var code = t.file("sample/shapes.kern")
/// </code>
/// </summary>
[KernType("transpiler")]
public sealed class KernTranspiler
{
	private readonly Transpiler _inner;

	internal KernTranspiler(string target) => _inner = new Transpiler(target);

	public string Target => _inner.Target;

	public KernTranspiler MapScript(string path)
	{
		_inner.AddScript(path);
		return this;
	}

	public string Text(string source)
	{
		TranspileResult result = _inner.RunText("main", source);
		return result.Files
			.Where(file => !Path.GetFileName(file.Key).StartsWith("KernRuntime", StringComparison.Ordinal))
			.Select(file => file.Value)
			.FirstOrDefault() ?? string.Empty;
	}

	public string File(string path)
	{
		TranspileResult result = _inner.RunPath(path);
		var text = new System.Text.StringBuilder();
		foreach ((string name, string content) in result.Files)
		{
			if (Path.GetFileName(name).StartsWith("KernRuntime", StringComparison.Ordinal))
				continue;
			text.Append("// ==== ").Append(name).Append(" ====\n").Append(content).Append('\n');
		}

		return text.ToString();
	}

	public IReadOnlyList<string> Project(string path, string outDir)
	{
		TranspileResult result = _inner.RunPath(path);
		string root = Path.GetFullPath(outDir);
		Directory.CreateDirectory(root);
		var written = new List<string>();
		foreach ((string name, string content) in result.Files)
		{
			string target = Path.Combine(root, name);
			Directory.CreateDirectory(Path.GetDirectoryName(target)!);
			System.IO.File.WriteAllText(
				target, content.Replace("\r\n", "\n"), new System.Text.UTF8Encoding(false));
			written.Add(target);
		}

		return written;
	}

	public bool IsKotlin => _inner.Target == "kotlin";
}

[KernType("transpiler")]
public static class TranspilerApi
{
	/// <summary><c>transpiler("csharp")</c> / <c>transpiler("kotlin")</c>.</summary>
	[KernCtor]
	[KernName("transpiler")]
	public static KernTranspiler Create(string target) => new(target);

	[KernProperty]
	public static string Target(KNativeBox self) => Require(self).Target;

	[KernMember]
	[KernName("map_script")]
	public static KernTranspiler MapScript(KNativeBox self, string path) => Require(self).MapScript(path);

	[KernMember]
	[KernReturn("result<str, str>")]
	public static KResult<string, string> Text(KNativeBox self, string source) => Guard(() => Require(self).Text(source));

	[KernMember]
	[KernReturn("result<str, str>")]
	public static KResult<string, string> File(KNativeBox self, string path) => Guard(() => Require(self).File(path));

	[KernMember]
	[KernReturn("result<list<str>, str>")]
	public static KResult<KList<string>, string> Project(KNativeBox self, string path, string outDir)
	{
		try
		{
			IReadOnlyList<string> files = Require(self).Project(path, outDir);
			KValue list = KValue.Obj(new KList(null, files.Select(KValue.Str)));
			return KResult<KList<string>, string>.Success(new KList<string>(list));
		}
		catch (Exception exception)
		{
			return KResult<KList<string>, string>.Failure(exception.Message);
		}
	}

	private static KResult<string, string> Guard(Func<string> body)
	{
		try
		{
			return KResult<string, string>.Success(body());
		}
		catch (Exception exception)
		{
			return KResult<string, string>.Failure(exception.Message);
		}
	}

	private static KernTranspiler Require(KNativeBox self) => self.Value switch
	{
		KernTranspiler transpiler => transpiler,
		_ => throw new KernPanic($"transpiler: ожидался транспилятор, получено {self.Value.GetType().Name}"),
	};
}
