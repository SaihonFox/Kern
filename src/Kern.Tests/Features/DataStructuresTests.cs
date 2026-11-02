using Kern.Tests.Features.Support;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>
/// M14: структуры данных stage 1 (spec §16): <c>deque</c>, <c>priority_queue</c>,
/// <c>sorted_set</c>, <c>sorted_dict</c>, <c>bitset</c>, <c>ring_buffer</c>,
/// <c>lazy</c>, <c>frozen_list/dict/set</c>, <c>span</c>.
/// </summary>
public sealed class DataStructuresTests
{
	[Fact]
	public void Deque_pushes_and_pops_both_ends()
	{
		const string source = """
			fun ends() -> i32 {
				deque<i32> d()
				d.push_back(2)
				d.push_front(1)
				d.push_back(3)
				i32 front = d.pop_front()
				i32 back = d.pop_back()
				front * 100 + back * 10 + d.peek_front()!
			}

			fun as_collection() -> i32 {
				deque<i32> d()
				d.push_back(7)
				d.push_back(8)
				d.len * 10 + d.pop_back()
			}
			""";

		Assert.Equal(KValue.I32(132), StdPipeline.Run(source, "ends"));
		Assert.Equal(KValue.I32(28), StdPipeline.Run(source, "as_collection"));
	}

	[Fact]
	public void Deque_empty_pop_panics_in_russian()
	{
		const string source = """
			fun run() -> i32 {
				deque<i32> d()
				d.pop_front()
			}
			""";

		KernPanic panic = StdPipeline.RunPanics(source, "run");
		Assert.Contains("pop_front: пустая очередь", panic.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Priority_queue_returns_maximum_and_lists_in_priority_order()
	{
		const string source = """
			fun run() -> i32 {
				priority_queue<i32> q()
				q.push(3)
				q.push(9)
				q.push(1)
				q.push(5)
				i32 top = q.pop()
				i32 nxt = q.peek()!
				list<i32> order = q.to_list()
				top * 1000 + nxt * 100 + order[0] * 10 + order[1] + q.len
			}
			""";

		// 9*1000 + 5*100 + 5*10 + 3 + 3 = 9556
		Assert.Equal(KValue.I32(9556), StdPipeline.Run(source, "run"));
	}

	[Fact]
	public void Priority_queue_uses_declared_compare()
	{
		const string source = """
			type Score(i32 points) {
				op compare(Score other) -> i32 $points - other.points
			}

			fun run() -> i32 {
				priority_queue<Score> q()
				q.push(Score(3))
				q.push(Score(8))
				q.push(Score(5))
				Score top = q.pop()
				top.points
			}
			""";

		Assert.Equal(KValue.I32(8), StdPipeline.Run(source, "run"));
	}

	[Fact]
	public void Priority_queue_with_unsupported_element_type_panics_in_russian()
	{
		const string source = """
			fun run() -> i32 {
				priority_queue<list<i32>> q()
				q.push([1])
				q.push([2])
				q.len
			}
			""";

		KernPanic panic = StdPipeline.RunPanics(source, "run");
		Assert.Contains("сравнение", panic.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Sorted_set_keeps_order_and_deduplicates()
	{
		const string source = """
			fun run() -> i32 {
				sorted_set<i32> s()
				s.add(3)
				s.add(1)
				s.add(2)
				s.add(1)
				list<i32> items = s.to_list()
				i32 order = items[0] * 100 + items[1] * 10 + items[2]
				bool removed = s.remove(2)
				i32 flags = if removed { 1 } else { 0 }
				flags + if s.contains(1) { 2 } else { 0 } + if s.contains(2) { 0 } else { 4 }
				order * 10 + flags
			}
			""";

		// 123 * 10 + (1 + 2 + 4) = 1237
		Assert.Equal(KValue.I32(1231), StdPipeline.Run(source, "run"));
	}

	[Fact]
	public void Sorted_dict_orders_keys_and_supports_operations()
	{
		const string source = """
			fun run() -> i32 {
				sorted_dict<str, i32> d()
				d.set("b", 2)
				d.set("a", 1)
				d.set("c", 3)
				d.set("a", 10)
				list<str> keys = d.keys()
				i32 ordered = if keys[0] == "a" && keys[1] == "b" && keys[2] == "c" { 1 } else { 0 }
				d.get("a") * 1000 + d.get_or("z", 7) * 100 + d.len * 10 + ordered
			}

			fun removal() -> i32 {
				sorted_dict<str, i32> d()
				d.set("b", 2)
				bool before = d.contains_key("b")
				bool after = d.remove("b")
				bool still = d.contains_key("b")
				if before && after && !still { 1 } else { 0 }
			}
			""";

		// 10*1000 + 7*100 + 3*10 + 1 = 10731
		Assert.Equal(KValue.I32(10731), StdPipeline.Run(source, "run"));
		Assert.Equal(KValue.I32(1), StdPipeline.Run(source, "removal"));
	}

	[Fact]
	public void Sorted_dict_missing_key_panics_in_russian()
	{
		const string source = """
			fun run() -> i32 {
				sorted_dict<str, i32> d()
				d.get("nope")
			}
			""";

		KernPanic panic = StdPipeline.RunPanics(source, "run");
		Assert.Contains("sorted_dict: ключ nope отсутствует", panic.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Bitset_sets_flips_and_counts_bits()
	{
		const string source = """
			fun run() -> i32 {
				bitset b = bitset(8)
				b.set(1)
				b.set(3)
				b.set(7)
				i32 flags = 0
				if b.get(1) { flags += 1 }
				if b.get(2) == false { flags += 2 }
				b.flip(1)
				b.clear(3)
				i32 second = b.count
				b.clear_all()
				b.count * 100 + second * 10 + b.len
			}
			""";

		// После flip(1) и clear(3) остаётся только бит 7: 1*10; затем clear_all обнуляет.
		// 0*100 + 1*10 + 8 = 18
		Assert.Equal(KValue.I32(18), StdPipeline.Run(source, "run"));
	}

	[Fact]
	public void Bitset_boolean_operations_and_bounds()
	{
		const string source = """
			fun run() -> i32 {
				bitset a = bitset(8)
				a.set(0)
				a.set(2)
				bitset c = bitset(8)
				c.set(2)
				c.set(4)
				i32 union_count = a.or(c).count
				i32 inter_count = a.and(c).count
				i32 diff_count = a.xor(c).count
				i32 inverse_count = a.not().count
				i32 source_count = a.count
				union_count * 1000 + inter_count * 100 + diff_count * 10 + inverse_count + source_count
			}

			fun out_of_range() -> bool {
				bitset b = bitset(8)
				b.set(9)
				true
			}

			fun size_mismatch() -> i32 {
				bitset a = bitset(8)
				bitset b = bitset(4)
				a.and(b).count
			}
			""";

		// 3*1000 + 1*100 + 2*10 + 6 + 2 = 3128
		Assert.Equal(KValue.I32(3128), StdPipeline.Run(source, "run"));
		KernPanic bounds = StdPipeline.RunPanics(source, "out_of_range");
		Assert.Contains("bitset: индекс 9 вне диапазона", bounds.Message, StringComparison.Ordinal);
		KernPanic size = StdPipeline.RunPanics(source, "size_mismatch");
		Assert.Contains("bitset: размеры не совпадают", size.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Ring_buffer_overwrites_the_oldest_element()
	{
		const string source = """
			fun order() -> i32 {
				ring_buffer<i32> r = ring_buffer(3)
				r.push(1)
				r.push(2)
				r.push(3)
				r.push(4)
				list<i32> items = r.to_list()
				items[0] * 100 + items[1] * 10 + items[2]
			}

			fun state() -> i32 {
				ring_buffer<i32> r = ring_buffer(3)
				r.push(1)
				r.push(2)
				r.push(3)
				r.push(4)
				i32 first = r.pop()!
				i32 peeked = r.peek()!
				first * 10 + peeked + r.capacity * 100 + r.len * 10
			}

			fun empty() -> bool {
				ring_buffer<i32> r = ring_buffer(2)
				r.pop() == nil && r.peek() == nil && r.is_empty
			}
			""";

		Assert.Equal(KValue.I32(234), StdPipeline.Run(source, "order"));
		// 2*10 + 3 + 3*100 + 2*10 = 343
		Assert.Equal(KValue.I32(343), StdPipeline.Run(source, "state"));
		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "empty"));
	}

	[Fact]
	public void Lazy_computes_once_under_lock()
	{
		const string source = """
			fun run() -> i32 {
				var calls = 0
				lazy<i32> slow = lazy(fun -> i32 {
					calls += 1
					41
				})
				i32 first = slow.value
				i32 second = slow.value
				first + second + calls
			}

			fun ready() -> bool {
				var calls = 0
				lazy<i32> slow = lazy(fun -> i32 {
					calls += 1
					7
				})
				bool before = slow.is_ready
				i32 value = slow.value
				!before && slow.is_ready && value == 7 && calls == 1
			}
			""";

		Assert.Equal(KValue.I32(83), StdPipeline.Run(source, "run"));
		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "ready"));
	}

	[Fact]
	public void Frozen_copies_are_independent_of_their_sources()
	{
		const string source = """
			fun list_copy() -> i32 {
				list<i32> source = [1, 2, 3]
				frozen_list<i32> frozen = frozen_list(source)
				source.add(4)
				source[0] = 9
				frozen.len * 10 + frozen.get(0)
			}

			fun set_copy() -> i32 {
				set<i32> source()
				source.add(1)
				source.add(2)
				frozen_set<i32> frozen = frozen_set(source)
				source.add(3)
				frozen.len * 10 + frozen.get(0)
			}

			fun dict_copy() -> i32 {
				dict<str, i32> source = ["b": 2, "a": 1]
				frozen_dict<str, i32> frozen = frozen_dict(source)
				source["c"] = 3
				i32 flags = if frozen.contains_key("c") { 0 } else { 1 }
				frozen.len * 10 + frozen.get("a") * 100 + flags
			}

			fun empty() -> bool {
				frozen_list<str> empty = frozen_list(<str>[])
				empty.is_empty && empty.len == 0
			}
			""";

		Assert.Equal(KValue.I32(31), StdPipeline.Run(source, "list_copy"));
		Assert.Equal(KValue.I32(21), StdPipeline.Run(source, "set_copy"));
		Assert.Equal(KValue.I32(121), StdPipeline.Run(source, "dict_copy"));
		Assert.Equal(KValue.Bool(true), StdPipeline.Run(source, "empty"));
	}

	[Fact]
	public void Span_mutates_the_source_and_validates_range()
	{
		const string source = """
			fun mutates() -> i32 {
				list<i32> values = [1, 2, 3, 4, 5]
				span<i32> view = span(values, 1, 3)
				view.set(0, 20)
				view.set(2, 40)
				values[1] * 100 + values[3] * 10 + view.len
			}

			fun full_view() -> i32 {
				list<i32> values = [1, 2, 3]
				span<i32> all = span(values)
				all.set(2, 9)
				values[2] * 10 + all.len
			}

			fun bad_range() -> i32 {
				list<i32> values = [1, 2, 3]
				span<i32> view = span(values, 1, 9)
				view.len
			}

			fun bad_index() -> i32 {
				list<i32> values = [1, 2, 3]
				span<i32> view = span(values)
				view.get(5)
			}
			""";

		// 20*100 + 40*10 + 3 = 2403
		Assert.Equal(KValue.I32(2403), StdPipeline.Run(source, "mutates"));
		Assert.Equal(KValue.I32(93), StdPipeline.Run(source, "full_view"));
		KernPanic range = StdPipeline.RunPanics(source, "bad_range");
		Assert.Contains("span: недопустимый диапазон", range.Message, StringComparison.Ordinal);
		KernPanic index = StdPipeline.RunPanics(source, "bad_index");
		Assert.Contains("span: индекс 5 вне среза", index.Message, StringComparison.Ordinal);
	}
}
