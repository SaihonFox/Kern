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
/// Нативные generic-типы <c>queue&lt;T&gt;</c> (FIFO) и <c>stack&lt;T&gt;</c> (LIFO)
/// (spec §16): фабрики без цели вызова, члены <c>enqueue/dequeue/peek</c> и
/// <c>push/pop/peek</c>, <c>len</c>/<c>is_empty</c>/<c>clear</c>/<c>to_list</c>,
/// подстановка <c>T</c> в параметры и результаты.
/// </summary>
public sealed class QueueStackTests
{
	[Fact]
	public void Queue_dequeues_in_fifo_order()
	{
		const string source = """
			fun value() -> i32 {
				queue<i32> q()
				q.enqueue(1)
				q.enqueue(2)
				q.enqueue(3)
				q.dequeue()
			}

			fun remaining() -> i32 {
				queue<i32> q()
				q.enqueue(1)
				q.enqueue(2)
				q.enqueue(3)
				q.dequeue()
				q.len
			}
			""";

		Assert.Equal(KValue.I32(1), Run(source, "value"));
		Assert.Equal(KValue.I32(2), Run(source, "remaining"));
	}

	[Fact]
	public void Queue_peek_looks_at_the_front_without_removing()
	{
		const string source = """
			fun value() -> i32 {
				queue<i32> q()
				q.enqueue(4)
				q.enqueue(5)
				i32 front = q.peek()
				front + q.len
			}
			""";

		Assert.Equal(KValue.I32(6), Run(source, "value"));
	}

	[Fact]
	public void Dequeue_on_empty_queue_panics_in_russian()
	{
		const string source = """
			fun value() -> i32 {
				queue<i32> q()
				q.dequeue()
			}
			""";

		var exception = Assert.Throws<KernPanic>(() => Run(source, "value"));
		Assert.Contains("dequeue: пустая очередь", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Stack_pops_in_lifo_order()
	{
		const string source = """
			fun value() -> i32 {
				stack<i32> s()
				s.push(1)
				s.push(2)
				s.push(3)
				s.pop()
			}

			fun peeked() -> i32 {
				stack<i32> s()
				s.push(1)
				s.push(2)
				i32 top = s.peek()
				top + s.len
			}

			fun remaining() -> i32 {
				stack<i32> s()
				s.push(1)
				s.push(2)
				s.pop()
				s.len
			}
			""";

		Assert.Equal(KValue.I32(3), Run(source, "value"));
		Assert.Equal(KValue.I32(4), Run(source, "peeked"));
		Assert.Equal(KValue.I32(1), Run(source, "remaining"));
	}

	[Fact]
	public void Pop_on_empty_stack_panics_in_russian()
	{
		const string source = """
			fun value() -> i32 {
				stack<i32> s()
				s.pop()
			}
			""";

		var exception = Assert.Throws<KernPanic>(() => Run(source, "value"));
		Assert.Contains("pop: пустой стек", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Queue_and_stack_substitute_the_element_type()
	{
		const string source = """
			fun queue_element() -> i32 {
				queue<str> q()
				q.enqueue("alpha")
				q.enqueue("beta")
				str first = q.dequeue()
				first.len
			}

			fun stack_element() -> i32 {
				stack<i32> s()
				s.push(4)
				s.push(6)
				i32 first = s.pop()
				i32 second = s.pop()
				first - second
			}
			""";

		Assert.Equal(KValue.I32(5), Run(source, "queue_element"));
		Assert.Equal(KValue.I32(2), Run(source, "stack_element"));
	}

	[Fact]
	public void Is_empty_and_clear_reset_the_containers()
	{
		const string source = """
			fun queue_states() -> i32 {
				queue<i32> q()
				i32 before = 0
				if q.is_empty {
					before = 1
				}
				q.enqueue(1)
				i32 stored = 0
				if q.is_empty == false {
					stored = 2
				}
				q.clear()
				before + stored + q.len
			}

			fun stack_states() -> i32 {
				stack<str> s()
				i32 before = 0
				if s.is_empty {
					before = 1
				}
				s.push("x")
				s.clear()
				before + s.len
			}
			""";

		Assert.Equal(KValue.I32(3), Run(source, "queue_states"));
		Assert.Equal(KValue.I32(1), Run(source, "stack_states"));
	}

	[Fact]
	public void To_list_keeps_order_and_yields_list_of_the_element_type()
	{
		const string source = """
			fun queue_order() -> i32 {
				queue<i32> q()
				q.enqueue(1)
				q.enqueue(2)
				q.enqueue(3)
				list<i32> items = q.to_list()
				items[0] * 10 + items[2]
			}

			fun stack_order() -> i32 {
				stack<i32> s()
				s.push(1)
				s.push(2)
				s.push(3)
				list<i32> items = s.to_list()
				items[0] * 10 + items[2]
			}
			""";

		Assert.Equal(KValue.I32(13), Run(source, "queue_order"));
		Assert.Equal(KValue.I32(13), Run(source, "stack_order"));
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

	/// <summary>Полный конвейер одного файла с сохранением диагностик.</summary>
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
