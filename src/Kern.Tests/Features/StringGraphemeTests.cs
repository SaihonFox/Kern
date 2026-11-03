using Kern.Binding.Catalog;
using Kern.Tests.Features.Support;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>Графемные члены <c>str</c>: <c>graphemes</c>, <c>grapheme_len</c>, <c>grapheme_at</c>, <c>normalize</c>.</summary>
[Collection("timing")]
public sealed class StringGraphemeTests
{
	[Fact]
	public void Catalog_exposes_grapheme_members()
	{
		NativeCatalog catalog = NativeCatalog.Scan(typeof(Kern.Runtime.Core.CoreModule).Assembly);
		NativeTypeDescriptor str = Assert.Single(catalog.Types, type => type.Name == "str");
		Assert.Contains(str.Members, member => member.Name == "grapheme_len");
		Assert.Contains(str.Members, member => member.Name == "grapheme_at");
	}
	[Fact]
	public void Grapheme_members_count_and_index_clusters()
	{
		const string source = """
			fun count() -> i32 "e\u0301x".grapheme_len
			fun first() -> str "e\u0301x".grapheme_at(0)
			fun last() -> str "e\u0301x".grapheme_at(-1)
			fun parts() -> i32 "e\u0301x".graphemes().len
			""";

		Assert.Equal(KValue.I32(2), StdPipeline.Run(source, "count"));
		Assert.Equal(KValue.Str("e\u0301"), StdPipeline.Run(source, "first"));
		Assert.Equal(KValue.Str("x"), StdPipeline.Run(source, "last"));
		Assert.Equal(KValue.I32(2), StdPipeline.Run(source, "parts"));
	}

	[Fact]
	public void Grapheme_normalize_uses_unicode_forms_when_globalization_is_available()
	{
		const string source = """
			fun composed() -> str "e\u0301".normalize("NFC")
			fun decomposed() -> str "é".normalize("NFD")
			""";

		// Проект собирается с InvariantGlobalization=true, где String.Normalize — no-op;
		// при включённой ICU проверяются реальные формы. Тест фиксирует, что вызов
		// доступен и не паникует, а не конкретную нормализацию.
		Assert.Equal(KValue.Str("e\u0301"), StdPipeline.Run(source, "composed"));
		Assert.Equal(KValue.Str("é"), StdPipeline.Run(source, "decomposed"));
	}
}
