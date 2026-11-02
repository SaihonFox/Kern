using System.Runtime.InteropServices;
using Kern.Compiler.Lexing;
using Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Attributes;

/// <summary>
/// Условная компиляция <c>#[if(os = "windows")]</c> (spec §7): объявление с ложным
/// условием не попадает в символы модуля, поэтому платформенные части можно держать
/// в одном файле без ветвлений в рантайме. Поддержаны ключи <c>os</c> (windows, linux,
/// macos/osx/darwin), <c>arch</c> (x64/x86/arm64/arm) и <c>target</c> (cli; wasm/native
/// пока всегда ложны). Несколько условий объединяются по «и».
/// </summary>
public static class ConditionalCompilation
{
	/// <summary>Атрибуты объявления (любого вида) без учёта конкретного типа узла.</summary>
	public static IReadOnlyList<AttributeNode> AttributesOf(SyntaxNode? node) => node switch
	{
		TypeDeclaration type => type.DeclaredAttributes,
		UnitDeclaration unit => unit.DeclaredAttributes,
		FunctionDeclaration function => function.DeclaredAttributes,
		FieldMemberNode field => field.DeclaredAttributes,
		PropertyMemberNode property => property.DeclaredAttributes,
		OperatorMemberNode @operator => @operator.DeclaredAttributes,
		ConstructorMemberNode constructor => constructor.DeclaredAttributes,
		ParentMethodSelectionNode selection => selection.DeclaredAttributes,
		ParentFieldSelectionNode fieldSelection => fieldSelection.DeclaredAttributes,
		GlobalFieldDeclaration global => global.DeclaredAttributes,
		ExtensionOperatorDeclaration extensionOperator => extensionOperator.DeclaredAttributes,
		ExtensionPropertyDeclaration extension => extension.DeclaredAttributes,
		UseDeclaration use => use.DeclaredAttributes,
		_ => [],
	};

	/// <summary>Объявление не исключено условием <c>#[if]</c>.</summary>
	public static bool IsIncluded(SyntaxNode? node) => IsIncluded(AttributesOf(node));

	public static bool IsIncluded(IReadOnlyList<AttributeNode>? attributes)
	{
		if (attributes is null || attributes.Count == 0)
			return true;
		foreach (var attribute in attributes)
		{
			if (!string.Equals(attribute.Name, "if", StringComparison.Ordinal))
				continue;
			if (!TryEvaluate(attribute, out _))
				return false;
		}
		return true;
	}

	/// <summary>
	/// Проверить аргументы <c>#[if]</c>: <c>key = "value"</c> через запятую. Возвращает
	/// ложь и русский текст ошибки для неподдержанного условия или неизвестной платформы.
	/// </summary>
	public static bool TryEvaluate(AttributeNode attribute, out string? error)
	{
		error = null;
		if (attribute.Arguments.Count == 0)
		{
			error = "#[if] требует условие, например #[if(os = \"windows\")]";
			return false;
		}

		bool result = true;
		for (int index = 0; index < attribute.Arguments.Count; index += 3)
		{
			if (index + 2 >= attribute.Arguments.Count ||
				!string.Equals(attribute.Arguments[index + 1], "=", StringComparison.Ordinal))
			{
				error = "#[if]: ожидалось условие вида os = \"windows\"";
				return false;
			}

			string key = attribute.Arguments[index];
			string raw = attribute.Arguments[index + 2];
			string value = raw.Length > 0 && raw[0] == '"' && StringLiteralText.TryDecode(raw, out string decoded, out _)
				? decoded
				: raw;
			if (!Matches(key, value, out bool matches, out error))
				return false;
			result &= matches;
		}
		return result;
	}

	private static bool Matches(string key, string value, out bool matches, out string? error)
	{
		error = null;
		matches = false;
		switch (key)
		{
			case "os":
				return MatchesOs(value, out matches, out error);
			case "arch":
				return MatchesArch(value, out matches, out error);
			case "target":
				return MatchesTarget(value, out matches, out error);
			default:
				error = $"#[if]: неизвестное условие '{key}' (поддержаны os, arch, target)";
				return false;
		}
	}

	private static bool MatchesOs(string value, out bool matches, out string? error)
	{
		error = null;
		switch (value)
		{
			case "windows": matches = OperatingSystem.IsWindows(); return true;
			case "linux": matches = OperatingSystem.IsLinux(); return true;
			case "macos" or "osx" or "darwin" or "mac": matches = OperatingSystem.IsMacOS(); return true;
			case "freebsd": matches = OperatingSystem.IsFreeBSD(); return true;
			default:
				matches = false;
				error = $"#[if]: неизвестная ОС '{value}' (windows, linux, macos, freebsd)";
				return false;
		}
	}

	private static bool MatchesArch(string value, out bool matches, out string? error)
	{
		error = null;
		Architecture architecture = RuntimeInformation.ProcessArchitecture;
		switch (value)
		{
			case "x64" or "x86_64" or "amd64": matches = architecture == Architecture.X64; return true;
			case "x86" or "i386": matches = architecture == Architecture.X86; return true;
			case "arm64" or "aarch64": matches = architecture == Architecture.Arm64; return true;
			case "arm": matches = architecture == Architecture.Arm; return true;
			default:
				matches = false;
				error = $"#[if]: неизвестная архитектура '{value}' (x64, x86, arm64, arm)";
				return false;
		}
	}

	private static bool MatchesTarget(string value, out bool matches, out string? error)
	{
		error = null;
		switch (value)
		{
			// Компилятор всегда собирает байткод для CLI-хоста; wasm/native появятся
			// вместе с соответствующими бэкендами, поэтому ветка всегда выключена.
			case "cli" or "vm": matches = true; return true;
			case "wasm" or "native": matches = false; return true;
			default:
				matches = false;
				error = $"#[if]: неизвестная цель '{value}' (cli, wasm, native)";
				return false;
		}
	}
}
