using Kern.Tests.Harness;
using Xunit;

namespace Kern.Tests.Execution;

public class RunTests
{
	[Theory]
	[InlineData(0)]
	[InlineData(2)]
	public void ArithmeticAndInterpolation(int opt)
	{
		string output = KernHarness.Run("""
			fun main {
				i32 a = 10
				println(2 ** 10)
				println(f"a=${a}")
			}
			""", opt);
		Assert.Equal("1024\na=10\n", output);
	}

	[Fact]
	public void SwapAndMultiDeclare()
	{
		Assert.Equal("4 397\n", KernHarness.Run("""
			fun main {
				i32 first = 397, second = 4
				first, second = second, first
				println(f"${first} ${second}")
			}
			"""));
	}

	[Fact]
	public void ResultAndMatch()
	{
		Assert.Equal("возраст 42\nошибка: пусто\n", KernHarness.Run("""
			fun parse(str t) -> result<i32, str> {
				if t == "" { ret Err("пусто") }
				Ok(t.to_i32() ?: 0)
			}
			fun show(result<i32, str> r) -> str
				match r {
					Ok(v) => f"возраст ${v}"
					Err(e) => f"ошибка: ${e}"
				}
			fun main {
				println(show(parse("42")))
				println(show(parse("")))
			}
			"""));
	}

	[Fact]
	public void UnitUnionEnumMatch()
	{
		Assert.Equal("красный\nзелёный\n", KernHarness.Run("""
			unit Red
			unit Green
			type Color = [Red, Green]
			fun name(Color c) -> str
				match c {
					Red => "красный"
					Green => "зелёный"
				}
			fun main {
				println(name(Red()))
				println(name(Green()))
			}
			"""));
	}

	[Fact]
	public void LocalFunctionsAndRecursion()
	{
		Assert.Equal("120\n", KernHarness.Run("""
			fun main {
				fun fact(i32 n) -> i32 if n <= 1 1 else n * fact(n - 1)
				println(fact(5))
			}
			"""));
	}

	[Fact]
	public void LoopReturnsList()
	{
		Assert.Equal("[1, 4, 9, 16]\n", KernHarness.Run("""
			fun main {
				list<i32> squares = loop i32 i in 1..5 i ** 2
				println(squares)
			}
			"""));
	}

	[Fact]
	public void ThreadsReturnResults()
	{
		Assert.Equal("144\n", KernHarness.Run("""
			use kern.threading[thread, worker]
			fun square(i32 n) -> i32 n * n
			fun main {
				worker<i32> w = thread.spawn_with(square, 12)
				match w.join() {
					Ok(v) => println(v)
					Err(e) => println(e)
				}
			}
			"""));
	}

	[Fact]
	public void ChannelBetweenThreads()
	{
		Assert.Equal("55\n", KernHarness.Run("""
			use kern.threading[thread, worker, channel]
			fun produce(channel<i32> ch) -> i32 {
				loop var i in 1..=4 { ch.send(i * i) }
				ch.close()
				4
			}
			fun main {
				channel<i32> ch(0)
				worker<i32> w = thread.spawn_with(produce, ch)
				i32 sum = 0
				loop 0..4 { sum += ch.recv() ?: 0 }
				w.join()
				println(sum + 25)
			}
			"""));
	}

	[Fact]
	public void RepeatOperator()
	{
		Assert.Equal("ababab|---|ababab|xx|1212||\n", KernHarness.Run("""
			fun main {
				str s = "ab" * 3
				str line = '-' * 3
				str rev = 3 * "ab"
				list<i32> xs = [1, 2] * 2
				str none = "q" * 0
				println(f"${s}|${line}|${rev}|${'x' * 2}|${xs[0]}${xs[1]}${xs[2]}${xs[3]}|${none}|")
			}
			"""));
	}

	[Fact]
	public void ThreadsShareMemory()
	{
		Assert.Equal("4000\n", KernHarness.Run("""
			use kern.threading[thread, worker, mutex]
			type Counter {
				i32 value = 0
				mutex gate = mutex()
				fun add(i32 n) {
					use var g = gate.guard()
					value += n
				}
			}
			fun main {
				var c = Counter()
				list<worker<i32>> ws = []
				loop var t in 0..4 {
					ws.add(thread.spawn(fun -> i32 {
						loop 0..1000 { c.add(1) }
						0
					}))
				}
				loop var w in ws { w.join() }
				println(c.value)
			}
			"""));
	}

	[Fact]
	public void ConcurrentDictAndParallelReduce()
	{
		Assert.Equal("3\n15\n", KernHarness.Run("""
			use kern.threading[conc_dict]
			use kern.tasks[parallel]
			async fun main {
				conc_dict<str, i32> d = conc_dict()
				await parallel.each(["a", "b", "a", "a"], fun(str w) { d.update(w, 0, fun(i32 n) -> i32 n + 1) })
				println(d.get("a"))
				println(await parallel.reduce([1, 2, 3, 4, 5], 0, fun(i32 a, i32 b) -> i32 a + b))
			}
			"""));
	}

	[Fact]
	public void GzipRoundTrip()
	{
		Assert.Equal("true\n", KernHarness.Run("""
			use kern.compress[compress]
			fun main {
				str text = "Kern Kern Kern Kern"
				match compress.gunzip(compress.gzip(text)) {
					Ok(back) => println(back == text)
					Err(e) => println(e)
				}
			}
			"""));
	}

	[Theory]
	[InlineData(0)]
	[InlineData(2)]
	public void WhileLoop(int opt)
	{
		Assert.Equal("0\n1\n2\n", KernHarness.Run("""
			fun main {
				i32 n = 0
				loop n < 3 {
					println(n)
					n += 1
				}
			}
			""", opt));
	}

	[Theory]
	[InlineData(0)]
	[InlineData(2)]
	public void InfiniteLoopWithStop(int opt)
	{
		Assert.Equal("4\n", KernHarness.Run("""
			fun main {
				i32 count = 0
				loop {
					count += 1
					if count >= 4 { stop }
				}
				println(count)
			}
			""", opt));
	}

	[Fact]
	public void WhileConditionWithInOperatorInParentheses()
	{
		Assert.Equal("4\n", KernHarness.Run("""
			fun main {
				list<i32> allowed = [1, 2, 3]
				i32 k = 1
				loop (k in allowed) k += 1
				println(k)
			}
			"""));
	}

	[Theory]
	[InlineData(0)]
	[InlineData(2)]
	public void StopAndNextInsideExpressionOperands(int opt)
	{
		Assert.Equal("[0, 2, 4, 6]\n", KernHarness.Run("""
			fun main {
				list<i32> picked = []
				loop var i in 0..10 {
					picked.add(i % 2 == 0 ? i : (i > 6 ? (stop) : (next)))
				}
				println(picked)
			}
			""", opt));
	}

	[Fact]
	public void StopInsideManyIterationsDoesNotLeakStack()
	{
		// 100 000 итераций с next посреди вызова: без RestoreSp стек бы переполнился
		Assert.Equal("50000\n", KernHarness.Run("""
			fun main {
				i32 n = 0
				loop var i in 0..100000 {
					n = n + (i % 2 == 0 ? 1 : (next))
				}
				println(n)
			}
			"""));
	}

	[Fact]
	public void MockRecordsCallsAndReturnsStubs()
	{
		Assert.Equal("2\n", KernHarness.Run("""
			use kern.test[mock]
			@type Svc {
				@fun get(i32 x) -> i32
			}
			fun main {
				Svc s = mock.create<Svc>()
				mock.returns(s, "get", 1)
				mock.returns_for(s, "get", [5], 2)
				println(s.get(0) + s.get(5) - s.get(0))
				mock.verify(s, "get", 3)
			}
			"""));
	}

	[Fact]
	public void AssignmentNarrowsNullableVariable()
	{
		Assert.Equal("101\n", KernHarness.Run("""
			fun main {
				i32? y
				if !y { y = 100 }
				println(y + 1)
			}
			"""));
	}

	[Fact]
	public void MatchBindingAfterNilArmIsNotNullable()
	{
		Assert.Equal("5\nнет\n", KernHarness.Run("""
			fun show(i32? v) -> str
				match v {
					nil => "нет"
					n => f"${n + 0}"
				}
			fun main {
				println(show(5))
				println(show(nil))
			}
			"""));
	}

	[Fact]
	public void StopMakesVariableNonNullAfterNilCheck()
	{
		Assert.Equal("3\n", KernHarness.Run("""
			fun main {
				list<i32?> xs = [1, 2, nil, 9]
				i32 sum = 0
				loop var x in xs {
					if x == nil { stop }
					sum += x
				}
				println(sum)
			}
			"""));
	}

	[Fact]
	public void KeywordNextCanBeMethodName()
	{
		Assert.Equal("1\n", KernHarness.Run("""
			type Counter {
				i32 n = 0
				fun next -> i32 {
					n += 1
					n
				}
			}
			fun main {
				Counter c()
				println(c.next())
			}
			"""));
	}

	[Fact]
	public void UnitDestructuresByFields()
	{
		Assert.Equal("1 2\n", KernHarness.Run("""
			unit Point(i32 x, i32 y)
			fun main {
				Point p(1, 2)
				(i32 a, i32 b) = p
				println(f"${a} ${b}")
			}
			"""));
	}

	[Fact]
	public void ListLiteralTakesElementTypeFromOverloadedParameter()
	{
		Assert.Equal("2\n", KernHarness.Run("""
			fun count(str sql) -> i32 0
			fun count(str sql, list<object> args) -> i32 args.len
			fun main {
				println(count("q", [1, "a"]))
			}
			"""));
	}

	[Fact]
	public void LoopAssignsExistingVariable()
	{
		Assert.Equal("2\n", KernHarness.Run("""
			fun main {
				i32 j = -1
				loop j in 0..3 { }
				println(j)
			}
			"""));
	}

	[Fact]
	public void LoopDeclaresWithVarAndRunsWithoutVariable()
	{
		Assert.Equal("[0, 1, 2]\n3\n", KernHarness.Run("""
			fun main {
				println(loop var i in 0..3 i)
				i32 count = 0
				loop 0..3 { count += 1 }
				println(count)
			}
			"""));
	}

	[Fact]
	public void LoopBareUndeclaredNameIsError()
	{
		var e = Assert.Throws<InvalidOperationException>(() => KernHarness.Compile("""
			fun main {
				loop i in 0..3 println(i)
			}
			"""));
		Assert.Contains("не объявлена", e.Message);
	}

	[Fact]
	public void MatchArmWithOldArrowIsError()
	{
		var e = Assert.Throws<InvalidOperationException>(() => KernHarness.Compile("""
			fun main {
				println(match 1 { 1 -> "a"; _ => "b" })
			}
			"""));
		Assert.Contains("=>", e.Message);
	}

	[Fact]
	public void DurationAndDatetimeArithmetic()
	{
		Assert.Equal("1m 30s\n3m\ntrue\n1d 12h\n", KernHarness.Run("""
			use kern.time[duration, datetime]
			fun main {
				var d = duration.from_seconds(90)
				println(d)
				println(d * 2)
				println(d > duration.from_minutes(1))
				var a = datetime.of(2026, 10, 14, 12, 30, 0)
				println(a + duration.from_hours(36) - a)
			}
			"""));
	}
}
