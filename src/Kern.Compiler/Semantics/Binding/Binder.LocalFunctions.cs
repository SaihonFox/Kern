using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Binding;

/// <summary>
/// Локальные функции (spec §3): <c>fun helper -> i32 …</c>. Функция разворачивается в
/// локальную переменную функционального типа со значением-лямбдой. Сигнатуры объявляются
/// до связывания тел (hoisting и взаимная рекурсия), поэтому <c>rec</c> может ссылаться сам
/// на себя: переменная живёт в ячейке, которую замыкание захватывает по ссылке.
/// </summary>
public sealed partial class Binder
{
	private void BindLocalFunctions(BlockStatement block, List<BoundStatement> statements)
	{
		var predeclared = new List<(LocalFunctionStatement Local, BoundVariable Variable, FunctionType Type)>();

		// Проход 1: явные сигнатуры объявляются заранее (для рекурсии).
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

			if (declaration.ReturnType is null || declaration.Parameters.Any(parameter => parameter.Type is null))
				continue;

			var parameters = new List<KernType>(declaration.Parameters.Count);
			var ok = true;
			foreach (var parameter in declaration.Parameters)
			{
				var type = _resolver.Resolve(parameter.Type!, _typeScope);
				if (IsError(type))
				{
					ok = false;
					break;
				}

				parameters.Add(type);
			}

			var returnType = _resolver.Resolve(declaration.ReturnType, _typeScope);
			if (!ok || IsError(returnType))
				continue;

			var functionType = new FunctionType(parameters, returnType);
			var variable = DeclareLocal(declaration.Name, functionType, false, local.Span);
			DeclareFlow(variable, true, functionType);
			predeclared.Add((local, variable, functionType));
		}

		// Проход 2: тела предобъявленных функций (видят свои и чужие имена).
		foreach (var (local, variable, functionType) in predeclared)
		{
			var declaration = local.Function;
			var value = BindLambda(
				new LambdaExpression(declaration.Parameters, declaration.ReturnType, declaration.Body, declaration.Span),
				functionType);
			if (IsError(value.Type))
				continue;

			statements.Add(new BoundLocalDeclaration(
				variable.Name,
				variable.Slot,
				functionType,
				value,
				false,
				local.Span));
		}

		// Запасной путь: сигнатура не выведена заранее (нет типа результата) — как раньше.
		foreach (var statement in block.Statements)
		{
			if (statement is not LocalFunctionStatement local)
				continue;
			if (predeclared.Any(item => ReferenceEquals(item.Local, local)) || local.Function.GenericArity > 0)
				continue;

			var declaration = local.Function;
			var value = BindLambda(
				new LambdaExpression(declaration.Parameters, declaration.ReturnType, declaration.Body, declaration.Span),
				hint: null);
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
