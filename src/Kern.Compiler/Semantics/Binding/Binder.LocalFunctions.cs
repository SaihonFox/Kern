using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Binding;

/// <summary>
/// Локальные функции (spec §3): <c>fun helper -> i32 …</c> внутри блока. Функция
/// разворачивается в локальную переменную функционального типа со значением-лямбдой и
/// объявляется в начале блока — поэтому её можно вызывать до текстового объявления
/// (hoisting) и она захватывает внешние локальные по значению. Взаимная/самовая рекурсия
/// пока не поддержана (имя ещё не объявлено в момент связывания тела).
/// </summary>
public sealed partial class Binder
{
	private void BindLocalFunctions(BlockStatement block, List<BoundStatement> statements)
	{
		foreach (var statement in block.Statements)
		{
			if (statement is not LocalFunctionStatement local)
				continue;

			var declaration = local.Function;
			if (declaration.GenericArity > 0)
			{
				Report(
					"KRN2109",
					$"Generic-локальная функция '{declaration.Name}' пока не поддержана",
					local.Span);
				continue;
			}

			var lambda = new LambdaExpression(
				declaration.Parameters,
				declaration.ReturnType,
				declaration.Body,
				declaration.Span);
			var value = BindLambda(lambda, hint: null);
			if (IsError(value.Type))
				continue;

			var variable = DeclareLocal(declaration.Name, value.Type, false, local.Span);
			DeclareFlow(variable, true, value.Type);
			statements.Add(new BoundLocalDeclaration(
				variable.Name,
				variable.Slot,
				value.Type,
				value,
				false,
				local.Span));
		}
	}
}
