namespace Kern.Compiler.Semantics.Types;

/// <summary>Общий текст типов с аргументами: <c>Name&lt;A,B&gt;</c> либо просто <c>Name</c>.</summary>
internal static class TypeText
{
	public static string WithArguments(string name, IReadOnlyList<KernType> arguments) =>
		arguments.Count == 0 ? name : $"{name}<{string.Join(",", arguments)}>";
}
