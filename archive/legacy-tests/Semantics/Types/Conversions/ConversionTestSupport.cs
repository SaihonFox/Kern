using Kern.Compiler.Semantics.Types;
using Xunit;
using TypeConversions = Kern.Compiler.Semantics.Types.Conversions.Conversions;
using Conversion = Kern.Compiler.Semantics.Types.Conversions.Conversion;

namespace Kern.Tests.Semantics.Types.Conversions;

/// <summary>Помощники для тестов классификации преобразований.</summary>
internal static class ConversionTestSupport
{
	public static BuiltinGenericType List(KernType element) =>
		new(BuiltinGenericKind.List, [element]);

	public static BuiltinGenericType Array(KernType element) =>
		new(BuiltinGenericKind.Array, [element]);

	public static BuiltinGenericType Set(KernType element) =>
		new(BuiltinGenericKind.Set, [element]);

	public static BuiltinGenericType Dict(KernType key, KernType value) =>
		new(BuiltinGenericKind.Dict, [key, value]);

	public static BuiltinGenericType Collection(KernType element) =>
		new(BuiltinGenericKind.Collection, [element]);

	public static Conversion Classify(KernType source, KernType target)
	{
		Assert.True(
			TypeConversions.TryClassify(source, target, out var conversion),
			$"Ожидалось неявное преобразование {source} -> {target}");
		Assert.True(conversion.IsImplicit);
		return conversion;
	}

	public static Conversion ClassifyContextual(KernType source, KernType target)
	{
		Assert.True(
			TypeConversions.TryClassifyContextual(source, target, out var conversion),
			$"Ожидалось контекстное преобразование {source} -> {target}");
		Assert.True(conversion.IsImplicit);
		return conversion;
	}

	public static void Rejects(KernType source, KernType target) =>
		Assert.False(
			TypeConversions.TryClassify(source, target, out _),
			$"Не ожидалось неявного преобразования {source} -> {target}");

	public static void RejectsContextual(KernType source, KernType target) =>
		Assert.False(
			TypeConversions.TryClassifyContextual(source, target, out _),
			$"Не ожидалось контекстного преобразования {source} -> {target}");
}
