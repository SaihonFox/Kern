using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Binding;

/// <summary>Запись в свойство нативного модуля: <c>log.level = "warn"</c> (spec §16).</summary>
public sealed partial class Binder
{
	private BoundStatement? TryBindModulePropertyAssignment(
		AssignmentStatement assignment,
		MemberAccessExpression member)
	{
		if (member.Target is not IdentifierExpression moduleTarget)
			return null;

		FunctionSymbol? setter = null;
		foreach (FunctionSymbol native in _module.NativeFunctions)
		{
			if (NativeModuleMatches(native.NativeModuleName, moduleTarget.Name) &&
				native.Name == member.Member &&
				native.Kind == FunctionKind.Setter &&
				native.Parameters.Count == 1)
			{
				setter = native;
				break;
			}
		}

		if (setter is null)
			return null;
		if (assignment.Operator != "=")
		{
			Report(
				"KRN2101",
				$"Составное присваивание свойству модуля '{member.Member}' не поддержано",
				assignment.Span);
			return new BoundBlock([], null, FallsThrough: true, assignment.Span);
		}

		KernType parameterType = setter.Parameters[0].Type;
		BoundExpression value = Convert(
			BindExpression(assignment.Value, parameterType),
			parameterType,
			assignment.Value.Span);
		if (IsError(value.Type))
			return new BoundBlock([], null, FallsThrough: true, assignment.Span);
		var call = new BoundCallExpression(setter, [value], PrimitiveType.Void, assignment.Span);
		return new BoundExpressionStatement(call, assignment.Span);
	}
}
