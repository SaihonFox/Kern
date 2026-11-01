using Kern.Binding.Attributes;

namespace Kern.Lib.Transpile;

/// <summary>
/// Модуль <c>kern.transpile</c>: транспилятор Kern → C# / Kotlin. Тип <c>transpiler</c>
/// создаётся функцией <c>transpiler("csharp")</c> (см. <see cref="Api.KernTranspiler"/>).
/// </summary>
[KernModule("kern.transpile")]
public static class TranspileModule
{
	[KernMember]
	public static string Version() => "0.1.0";
}
