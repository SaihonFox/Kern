using Kern.Bytecode;
using Kern.Tests.Harness;
using Xunit;

namespace Kern.Tests.Optimization;

public class OptimizerTests
{
	private const string Program = """
		fun classify(i32 n) -> str {
			if n < 0 { ret "минус" }
			if n == 0 { ret "ноль" }
			n % 2 == 0 ? "чёт" : "нечёт"
		}

		fun sum_to(i32 n) -> i32 {
			i32 total = 0
			loop var i in 1..=n { total += i }
			total
		}

		fun main {
			println(classify(-3))
			println(classify(0))
			println(classify(7))
			println(classify(10))
			println(sum_to(100))
			loop var i in 0..6 {
				if i % 2 == 1 { next }
				if i > 4 { stop }
				println(i)
			}
			bool flag = true
			if !flag { println("нет") } else { println("да") }
			println(match 5 { 1 => "a"; 5 => "five"; _ => "z" })
		}
		""";

	[Fact]
	public void O2ProducesSameOutputAsO0()
	{
		string baseline = KernHarness.Run(Program, 0);
		Assert.Equal(baseline, KernHarness.Run(Program, 1));
		Assert.Equal(baseline, KernHarness.Run(Program, 2));
	}

	[Fact]
	public void O2DoesNotGrowCode()
	{
		int Total(KbcModule m) => m.Functions.Sum(f => f.Code.Length);
		Assert.True(Total(KernHarness.Compile(Program, 2)) <= Total(KernHarness.Compile(Program, 1)));
	}

	[Fact]
	public void O2RemovesVoidPopPairs()
	{
		var o1 = KernHarness.Compile(Program, 1);
		var o2 = KernHarness.Compile(Program, 2);
		int Pairs(KbcModule m) => m.Functions.Sum(f =>
		{
			int n = 0;
			for (int i = 0; i + 1 < f.Code.Length; i++)
				if (f.Code[i] == (int)OpCode.LdVoid && f.Code[i + 1] == (int)OpCode.Pop) n++;
			return n;
		});
		Assert.True(Pairs(o2) <= Pairs(o1));
	}

	[Fact]
	public void JumpTargetsStayInsideCode()
	{
		var m = KernHarness.Compile(Program, 2);
		foreach (var f in m.Functions)
			for (int pc = 0; pc < f.Code.Length;)
			{
				var op = (OpCode)f.Code[pc];
				int count = op == OpCode.Switch ? 1 + f.Code[pc + 1] : OpCodeInfo.OperandCount(op);
				pc += 1 + count;
			}
	}
}
