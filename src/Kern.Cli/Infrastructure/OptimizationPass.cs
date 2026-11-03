using System.Reflection;
using Kern.Bytecode;
using Kern.Compiler.Optimization;

namespace Kern.Cli.Infrastructure;

/// <summary>
/// Уровни оптимизации <c>-O0/-O1/-O2</c> (docs/ARCHITECTURE.md §9). Проходы живут в
/// <c>Kern.Compiler.Optimization</c> и подключаются по имени типа: пока прохода нет, сборка
/// продолжается без него, а <see cref="Note"/> объясняет, что запрошенная оптимизация не
/// применена. Так CLI не зависит от порядка, в котором появляются проходы компилятора.
/// </summary>
internal static class OptimizationPass
{
	public const string ConstantFolderType = "Kern.Compiler.Optimization.ConstantFolder";

	public const string PeepholeType = "Kern.Compiler.Optimization.PeepholeOptimizer";

	/// <summary>Применить проходы для уровня; <paramref name="note"/> — что не сработало.</summary>
	public static KbcModule Apply(KbcModule module, int level, out string? note)
	{
		ArgumentNullException.ThrowIfNull(module);
		note = null;
		if (level <= 0)
			return module;

		if (!TryApply(ConstantFolderType, "Fold", ref module, ref note))
			note = "оптимизация -O1: проход ConstantFolder ещё не подключён в компиляторе";
		if (level >= 2)
		{
			module = Inliner.Inline(module);
			TryApply(PeepholeType, "Optimize", ref module, ref note);
			module = DeadFunctionEliminator.Eliminate(module);
		}

		StackAnalyzer.Compute(module);
		return module;
	}

	private static bool TryApply(string typeName, string methodName, ref KbcModule module, ref string? note)
	{
		Type? type = Type.GetType($"{typeName}, Kern.Compiler");
		MethodInfo? method = type?
			.GetMethods(BindingFlags.Public | BindingFlags.Static)
			.FirstOrDefault(candidate =>
				candidate.Name == methodName &&
				candidate.GetParameters() is [{ ParameterType: var parameter }] &&
				parameter == typeof(KbcModule));

		if (method is null)
			return false;

		try
		{
			object? result = method.Invoke(null, [module]);
			if (result is KbcModule optimized)
				module = optimized;
			return true;
		}
		catch (TargetInvocationException exception)
		{
			throw new InvalidOperationException(
				$"проход оптимизации {typeName}.{methodName} завершился ошибкой: {exception.InnerException?.Message}",
				exception.InnerException);
		}
	}
}
