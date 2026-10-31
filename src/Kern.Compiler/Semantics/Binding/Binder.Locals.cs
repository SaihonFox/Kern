using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;

namespace Kern.Compiler.Semantics.Binding;

/// <summary>Объявления локальных переменных: тип, инициализатор, вывод типа (spec §3).</summary>
public sealed partial class Binder
{
	private BoundStatement BindLocalDeclaration(LocalDeclarationStatement declaration)
	{
		var declared = declaration.Type is null ? null : _resolver.Resolve(declaration.Type, _typeScope);
		var initializer = declaration.Initializer is null ? null : BindExpression(declaration.Initializer, declared);
		var initializerType = initializer?.Type;

		var type = declared ?? initializer?.Type ?? ErrorType.Instance;
		if (declared is null && initializer is null)
		{
			Report("KRN2102", $"У переменной '{declaration.Name}' нужен тип или инициализатор", declaration.Span);
			type = ErrorType.Instance;
		}
		else if (declared is null && initializerType is NilType)
		{
			Report("KRN2102", $"Нельзя вывести тип переменной '{declaration.Name}' из nil; укажите nullable-тип явно", declaration.Span);
			type = ErrorType.Instance;
		}
		else if (declared is null && initializerType is PrimitiveType { Primitive: PrimitiveKind.None })
		{
			Report("KRN2102", $"Нельзя вывести тип переменной '{declaration.Name}' из none; объявите переменную без инициализатора", declaration.Span);
			type = ErrorType.Instance;
		}

		if (initializer is not null && declared is not null)
			initializer = Convert(initializer, declared, declaration.Initializer!.Span);

		var variable = DeclareLocal(declaration.Name, type, declaration.IsSingleAssignment, declaration.Span);
		// Объявленный union важнее типа инициализатора: `result<i32, str> v = Ok(3)` должен
		// видеть оба варианта в match, а не сужаться до Ok<i32>.
		var flowType = declared is UnionType ? declared : initializerType;
		DeclareFlow(variable, initializer is not null, flowType);

		var additional = new List<BoundLocalBinding>(declaration.DeclaredBindings.Count);
		foreach (var binding in declaration.DeclaredBindings)
		{
			var extraInitializer = binding.Initializer is null ? null : BindExpression(binding.Initializer, declared);
			var extraInitializerType = extraInitializer?.Type;
			var extraType = declared ?? extraInitializer?.Type ?? ErrorType.Instance;
			if (declared is null && extraInitializerType is PrimitiveType { Primitive: PrimitiveKind.None })
			{
				Report("KRN2102", $"Нельзя вывести тип переменной '{binding.Name}' из none; объявите переменную без инициализатора", binding.Span);
				extraType = ErrorType.Instance;
			}
			if (extraInitializer is not null && declared is not null)
				extraInitializer = Convert(extraInitializer, declared, binding.Span);

			var extraVariable = DeclareLocal(binding.Name, extraType, declaration.IsSingleAssignment, binding.Span);
			DeclareFlow(extraVariable, extraInitializer is not null, extraInitializerType);
			additional.Add(new BoundLocalBinding(binding.Name, extraVariable.Slot, extraInitializer));
		}

		return new BoundLocalDeclaration(
			declaration.Name,
			variable.Slot,
			type,
			initializer,
			declaration.IsSingleAssignment,
			declaration.Span,
			additional);
	}
}
