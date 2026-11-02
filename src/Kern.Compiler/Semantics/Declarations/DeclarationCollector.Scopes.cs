using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Declarations;

public sealed partial class DeclarationCollector
{
	private TypeParameterScope BuildTypeScope(
		TypeSymbol symbol,
		IReadOnlyList<GenericParameterNode> parameters,
		string ownerDescription)
	{
		var scope = TypeParameterScope.Empty;
		var resolved = new List<TypeParameterType>(parameters.Count);
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var parameter in parameters)
		{
			CheckGenericParameter(parameter, seen, ownerDescription);
			var constraints = parameter.Constraints.Select(constraint => _resolver.Resolve(constraint, scope)).ToArray();
			var ctorArguments = parameter.DeclaredCtorArguments.Select(argument => _resolver.Resolve(argument, scope)).ToArray();
			var typeParameter = new TypeParameterType(
				parameter.Name, constraints, parameter.HasCtorConstraint, ctorArguments);
			scope = scope.Add(typeParameter);
			resolved.Add(typeParameter);
		}

		symbol.GenericParameters = resolved;
		_typeScopes[symbol] = scope;
		return scope;
	}

	private (IReadOnlyList<TypeParameterType> Parameters, TypeParameterScope Scope) BuildFunctionScope(
		IReadOnlyList<GenericParameterNode> parameters,
		TypeParameterScope outer,
		string ownerDescription,
		HashSet<string>? seen = null)
	{
		var scope = outer;
		var resolved = new List<TypeParameterType>(parameters.Count);
		seen ??= new HashSet<string>(StringComparer.Ordinal);
		foreach (var parameter in parameters)
		{
			CheckGenericParameter(parameter, seen, ownerDescription);
			var constraints = parameter.Constraints.Select(constraint => _resolver.Resolve(constraint, scope)).ToArray();
			var ctorArguments = parameter.DeclaredCtorArguments.Select(argument => _resolver.Resolve(argument, scope)).ToArray();
			var typeParameter = new TypeParameterType(
				parameter.Name, constraints, parameter.HasCtorConstraint, ctorArguments);
			scope = scope.Add(typeParameter);
			resolved.Add(typeParameter);
		}
		return (resolved, scope);
	}

	private void CheckGenericParameter(GenericParameterNode parameter, HashSet<string> seen, string ownerDescription)
	{
		if (!seen.Add(parameter.Name))
			Report("KRN1408", $"Повторный generic-параметр '{parameter.Name}' у {ownerDescription}", parameter.Span);

		if (IsKnownTypeName(parameter.Name))
		{
			Report(
				"KRN1409",
				$"Generic-параметр '{parameter.Name}' скрывает тип с тем же именем",
				parameter.Span,
				DiagnosticSeverity.Warning);
		}
	}

	private bool IsKnownTypeName(string name) => _resolver.IsTypeName(name);

	private void CheckMemberConflicts(
		TypeSymbol owner,
		IReadOnlyList<FieldSymbol> fields,
		IReadOnlyList<PropertySymbol> properties,
		IReadOnlyList<FunctionSymbol> methods,
		IReadOnlyList<FunctionSymbol> parentSelections,
		IReadOnlyList<FunctionSymbol> operators,
		IReadOnlyList<FunctionSymbol> constructors)
	{
		var names = new Dictionary<string, TextSpan>(StringComparer.Ordinal);
		void Register(string name, TextSpan span)
		{
			if (names.TryAdd(name, span))
				return;
			Report("KRN1407", $"Повторное имя члена '{name}' в типе '{owner.Name}'", span);
		}

		foreach (var field in fields)
			Register(field.Name, SpanOf(field.Declaration, owner));
		foreach (var property in properties)
			Register(property.Name, SpanOf(property.Declaration, owner));

		// Функции перегружаются между собой, поэтому имя регистрирует только первая перегрузка.
		var methodNames = new HashSet<string>(StringComparer.Ordinal);
		foreach (var method in methods)
			if (methodNames.Add(method.Name))
				Register(method.Name, SpanOf(method.Declaration, owner));
		foreach (var selection in parentSelections)
			if (methodNames.Add(selection.Name))
				Register(selection.Name, SpanOf(selection.Declaration, owner));

		var signatures = new Dictionary<string, FunctionSymbol>(StringComparer.Ordinal);
		foreach (var function in methods.Concat(parentSelections).Concat(operators))
		{
			if (signatures.TryAdd(function.Signature, function))
				continue;
			var what = function.IsOperator
				? $"оператора '{function.OpSymbol}'"
				: $"функции '{function.Name}'";
			Report("KRN1400", $"Повторная сигнатура {what}", SpanOf(function.Declaration, owner));
		}

		var constructorSignatures = new HashSet<string>(StringComparer.Ordinal);
		foreach (var constructor in constructors)
		{
			if (constructorSignatures.Add(constructor.Signature))
				continue;
			Report("KRN1400", "Повторная сигнатура конструктора", SpanOf(constructor.Declaration, owner));
		}
	}

	private static TextSpan SpanOf(SyntaxNode? declaration, TypeSymbol owner) =>
		declaration?.Span ?? owner.Declaration?.Span ?? default;

	private void Report(
		string code,
		string message,
		TextSpan span,
		DiagnosticSeverity severity = DiagnosticSeverity.Error) =>
		_diagnostics.Add(new Diagnostic(code, message, span, severity));
}
