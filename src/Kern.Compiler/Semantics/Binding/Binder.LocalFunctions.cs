using System.Collections;
using System.Reflection;
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
	private HashSet<LocalFunctionStatement> BindLocalFunctions(BlockStatement block, List<BoundStatement> statements)
	{
		var predeclared = new List<(LocalFunctionStatement Local, BoundVariable Variable, FunctionType Type, bool Hoisted)>();
		var hoisted = new HashSet<LocalFunctionStatement>(ReferenceEqualityComparer.Instance);

		// Проход 1: явные сигнатуры объявляются заранее (для рекурсии и взаимных вызовов).
		for (var index = 0; index < block.Statements.Count; index++)
		{
			if (block.Statements[index] is not LocalFunctionStatement local)
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

			var functionType = new FunctionType(parameters, CallResultTypeForAsync(declaration.IsAsync, returnType));
			var variable = DeclareLocal(declaration.Name, functionType, false, local.Span);
			DeclareFlow(variable, true, functionType);
			bool isHoisted = MentionsBefore(block.Statements, index, declaration.Name);
			predeclared.Add((local, variable, functionType, isHoisted));
			if (isHoisted)
				hoisted.Add(local);
		}

		// Проход 2: тело поднимается в начало блока только если имя использовано раньше
		// объявления (spec §3); иначе тело связывается на месте и видит предыдущие переменные.
		foreach (var (local, variable, functionType, isHoisted) in predeclared)
		{
			if (!isHoisted)
				continue;

			var declaration = local.Function;
			var value = BindLambda(
				new LambdaExpression(declaration.Parameters, declaration.ReturnType, declaration.Body, declaration.Span)
				{
					IsAsync = declaration.IsAsync,
					HasYield = declaration.HasYield
				},
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

		return hoisted;
	}

	/// <summary>Упоминается ли имя в операторах блока до объявления локальной функции.</summary>
	private static bool MentionsBefore(IReadOnlyList<StatementNode> statements, int index, string name)
	{
		for (var earlier = 0; earlier < index; earlier++)
			if (SyntaxMentions(statements[earlier], name, new HashSet<object>(ReferenceEqualityComparer.Instance)))
				return true;
		return false;
	}

	private static bool SyntaxMentions(SyntaxNode node, string name, HashSet<object> visited)
	{
		if (!visited.Add(node))
			return false;

		foreach (var property in PublicProperties(node.GetType()))
		{
			object? value;
			try
			{
				value = property.GetValue(node);
			}
			catch (TargetInvocationException)
			{
				continue;
			}

			if (value is IdentifierExpression identifier && identifier.Name == name)
				return true;
			if (value is SyntaxNode nested && SyntaxMentions(nested, name, visited))
				return true;
			if (value is IEnumerable enumerable and not string)
			{
				foreach (object? item in enumerable)
				{
					if (item is IdentifierExpression nestedIdentifier && nestedIdentifier.Name == name)
						return true;
					if (item is SyntaxNode child && SyntaxMentions(child, name, visited))
						return true;
				}
			}
		}

		return false;
	}

	private static readonly Dictionary<Type, PropertyInfo[]> PropertyCache = [];

	private static PropertyInfo[] PublicProperties(Type type)
	{
		lock (PropertyCache)
		{
			if (PropertyCache.TryGetValue(type, out PropertyInfo[]? cached))
				return cached;
			PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
			PropertyCache[type] = properties;
			return properties;
		}
	}

	/// <summary>
	/// Тело локальной функции связывается в точке объявления (после ранее объявленных
	/// переменных блока). Предобъявленная сигнатура переиспользуется; без неё тип
	/// выводится из тела-лямбды.
	/// </summary>
	private void BindLocalFunctionAt(LocalFunctionStatement local, List<BoundStatement> statements)
	{
		var declaration = local.Function;
		if (declaration.GenericArity > 0)
			return;

		if (_scope.Lookup(declaration.Name) is { } predeclared &&
			predeclared.Type is FunctionType functionType &&
			predeclared.Frame == _frame)
		{
			var value = BindLambda(
				new LambdaExpression(declaration.Parameters, declaration.ReturnType, declaration.Body, declaration.Span)
				{
					IsAsync = declaration.IsAsync,
					HasYield = declaration.HasYield
				},
				functionType);
			if (IsError(value.Type))
				return;

			statements.Add(new BoundLocalDeclaration(
				predeclared.Name,
				predeclared.Slot,
				functionType,
				value,
				false,
				local.Span));
			return;
		}

		var inferred = BindLambda(
			new LambdaExpression(declaration.Parameters, declaration.ReturnType, declaration.Body, declaration.Span)
			{
				IsAsync = declaration.IsAsync,
				HasYield = declaration.HasYield
			},
			hint: null);
		if (IsError(inferred.Type))
			return;

		var variable = DeclareLocal(declaration.Name, inferred.Type, false, local.Span);
		DeclareFlow(variable, true, inferred.Type);
		statements.Add(new BoundLocalDeclaration(
			variable.Name,
			variable.Slot,
			inferred.Type,
			inferred,
			false,
			local.Span));
	}
}
