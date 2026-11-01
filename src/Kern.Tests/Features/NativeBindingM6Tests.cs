using Kern.Binding.Catalog;
using Kern.Binding.Resolution;
using Kern.Compiler.CodeGen;
using Kern.Compiler.Native;
using Kern.Compiler.Semantics;
using Kern.Compiler.Semantics.Binding;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// M6: общий протокол <c>collection&lt;T&gt;</c> (list/array/dict/str/set/queue/stack/observable),
/// <c>observable&lt;T&gt;</c>, нативное наследование, <c>kern.text</c> и <c>kern.numerics</c>.
/// </summary>
public sealed class NativeBindingM6Tests
{
	[Fact]
	public void Collection_accepts_every_native_container_and_iterates()
	{
		const string source = """
			fun size(collection<i32> c) -> i32 c.len
			fun chars_size(collection<char> c) -> i32 c.len
			fun empty(collection<i32> c) -> bool c.is_empty
			fun has_two(collection<i32> c) -> bool 2 in c
			fun sum(collection<i32> c) -> i32 {
				var total = 0
				loop var x in c {
					total += x
				}
				total
			}

			fun list_size() -> i32 size([1, 2, 3])
			fun set_size() -> i32 {
				set<i32> s = set<i32>()
				s.add(2)
				size(s)
			}
			fun queue_size() -> i32 {
				queue<i32> q = queue<i32>()
				q.enqueue(1)
				q.enqueue(2)
				size(q)
			}
			fun stack_size() -> i32 {
				stack<i32> st = stack<i32>()
				st.push(2)
				size(st)
			}
			fun observable_size() -> i32 {
				observable<i32> o = observable<i32>()
				o.add(2)
				size(o)
			}
			fun string_size() -> i32 {
				collection<char> word = "привет"
				chars_size(word)
			}
			fun string_has_t() -> bool {
				collection<char> word = "привет"
				'т' in word
			}
			fun set_has_two() -> bool {
				set<i32> s = set<i32>()
				s.add(2)
				has_two(s)
			}
			fun queue_sum() -> i32 {
				queue<i32> q = queue<i32>()
				q.enqueue(10)
				q.enqueue(20)
				sum(q)
			}
			fun empty_list() -> bool empty(<i32>[])
			""";

		Assert.Equal(KValue.I32(3), Run(source, "list_size"));
		Assert.Equal(KValue.I32(1), Run(source, "set_size"));
		Assert.Equal(KValue.I32(2), Run(source, "queue_size"));
		Assert.Equal(KValue.I32(1), Run(source, "stack_size"));
		Assert.Equal(KValue.I32(1), Run(source, "observable_size"));
		Assert.Equal(KValue.I32(6), Run(source, "string_size"));
		Assert.Equal(KValue.Bool(true), Run(source, "string_has_t"));
		Assert.Equal(KValue.Bool(true), Run(source, "set_has_two"));
		Assert.Equal(KValue.I32(30), Run(source, "queue_sum"));
		Assert.Equal(KValue.Bool(true), Run(source, "empty_list"));
	}

	[Fact]
	public void Observable_notifies_listeners_on_changes()
	{
		const string source = """
			fun events() -> str {
				observable<i32> o = observable<i32>()
				var log = ""
				o.on_change(fun(str kind, i32 item) { log += f"${kind}:${item};" })
				o.add(1)
				o.add(2)
				o.remove(1)
				o.clear()
				log
			}
			fun collection_view() -> i32 {
				observable<i32> o = observable<i32>()
				o.add(7)
				o.add(8)
				collection<i32> c = o
				c.len
			}
			""";

		Assert.Equal(KValue.Str("add:1;add:2;remove:1;clear:none;"), Run(source, "events"));
		Assert.Equal(KValue.I32(2), Run(source, "collection_view"));
	}

	[Fact]
	public void Scientific_and_exact_numbers_work()
	{
		const string source = """
			fun decimal_sum() -> str {
				decimal a = decimal("1.5")
				decimal b = decimal("2.25")
				f"${a + b}"
			}
			fun decimal_compare() -> bool {
				decimal a = decimal("1.5")
				a < decimal("2")
			}
			fun bigint_sum() -> str {
				bigint big = bigint("12345678901234567890")
				f"${big + big}"
			}
			fun bigint_pow() -> str {
				bigint big = bigint("10")
				f"${big.pow(20)}"
			}
			fun bigint_compare() -> bool {
				bigint("10") < bigint("11")
			}
			""";

		Assert.Equal(KValue.Str("3.75"), Run(source, "decimal_sum"));
		Assert.Equal(KValue.Bool(true), Run(source, "decimal_compare"));
		Assert.Equal(KValue.Str("24691357802469135780"), Run(source, "bigint_sum"));
		Assert.Equal(KValue.Str("100000000000000000000"), Run(source, "bigint_pow"));
		Assert.Equal(KValue.Bool(true), Run(source, "bigint_compare"));
	}

	[Fact]
	public void Text_module_covers_everyday_operations()
	{
		const string source = """
			fun upper() -> str text.upper("kern")
			fun joined() -> str text.join(["a", "b", "c"], "-")
			fun parts() -> i32 text.split("a,b,c", ",").len
			fun parsed() -> i32 text.parse_i32("42") ?: -1
			fun bad_parse() -> bool text.parse_i32("x") == nil
			fun chars_roundtrip() -> str text.from_chars(text.chars("ok"))
			fun padded() -> str text.pad_left("7", 3, '0')
			fun lines() -> i32 text.lines("a\r\nb\nc").len
			""";

		Assert.Equal(KValue.Str("KERN"), Run(source, "upper"));
		Assert.Equal(KValue.Str("a-b-c"), Run(source, "joined"));
		Assert.Equal(KValue.I32(3), Run(source, "parts"));
		Assert.Equal(KValue.I32(42), Run(source, "parsed"));
		Assert.Equal(KValue.Bool(true), Run(source, "bad_parse"));
		Assert.Equal(KValue.Str("ok"), Run(source, "chars_roundtrip"));
		Assert.Equal(KValue.Str("007"), Run(source, "padded"));
		Assert.Equal(KValue.I32(3), Run(source, "lines"));
	}

	[Fact]
	public void Native_handles_inherit_from_their_csharp_base()
	{
		string directory = Path.Combine(Path.GetTempPath(), $"kern_m6_{Guid.NewGuid():N}");
		Directory.CreateDirectory(directory);
		string path1 = Path.Combine(directory, "probe1.txt").Replace('\\', '/');
		string path2 = Path.Combine(directory, "probe2.txt").Replace('\\', '/');
		string path3 = Path.Combine(directory, "probe3.txt").Replace('\\', '/');
		try
		{
			var source = $$"""
				use kern.io[stream, streamwriter, streamreader]

				fun writer_is_stream() -> bool {
					use streamwriter w = streamwriter("{{path1}}")
					stream base = w
					base is streamwriter
				}
				fun writer_is_not_reader() -> bool {
					use streamwriter w = streamwriter("{{path2}}")
					stream base = w
					base is streamreader
				}
				fun writer_is_base_stream() -> bool {
					use streamwriter w = streamwriter("{{path3}}")
					w is stream
				}
				""";

			Assert.Equal(KValue.Bool(true), Run(source, "writer_is_stream"));
			Assert.Equal(KValue.Bool(false), Run(source, "writer_is_not_reader"));
			Assert.Equal(KValue.Bool(true), Run(source, "writer_is_base_stream"));
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	private static NativeCatalog Catalog() =>
		NativeCatalog.Scan(typeof(Kern.Runtime.Core.CoreModule).Assembly);

	private static KValue Run(string source, string function, params KValue[] arguments) =>
		new Interpreter(Compile(source).Module, new NativeResolver(Catalog())).Run(function, arguments);

	private static CodegenResult Compile(string source)
	{
		var outcome = CompileWithDiagnostics(source);
		Assert.DoesNotContain(outcome.Diagnostics, diagnostic => diagnostic.IsError);
		Assert.NotNull(outcome.Module);
		return outcome.Module!;
	}

	private sealed record Outcome(CodegenResult? Module, IReadOnlyList<Diagnostic> Diagnostics);

	private static Outcome CompileWithDiagnostics(string source)
	{
		var diagnostics = new List<Diagnostic>();
		var parsed = Parser.Parse(source);
		diagnostics.AddRange(parsed.Diagnostics);
		if (HasErrors(diagnostics))
			return new Outcome(null, diagnostics);

		var (table, tableDiagnostics) = SymbolTable.Collect(parsed.Tree);
		diagnostics.AddRange(tableDiagnostics);
		var (module, collectorDiagnostics) = DeclarationCollector.Collect(parsed.Tree, table, "main");
		diagnostics.AddRange(collectorDiagnostics);

		var catalog = Catalog();
		ModuleSymbol imported = NativeSymbolBuilder.Import(module, catalog, out var importProblems);
		Assert.Empty(importProblems);

		var bound = new Binder(imported, table).Bind();
		diagnostics.AddRange(bound.Diagnostics);
		if (HasErrors(diagnostics))
			return new Outcome(null, diagnostics);

		var generated = new CodeGenerator().Generate(bound);
		diagnostics.AddRange(generated.Diagnostics);
		return HasErrors(diagnostics)
			? new Outcome(null, diagnostics)
			: new Outcome(generated, diagnostics);
	}

	private static bool HasErrors(IReadOnlyList<Diagnostic> diagnostics) =>
		diagnostics.Any(diagnostic => diagnostic.IsError);
}
