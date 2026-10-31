using Kern.Compiler.Semantics.Attributes;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Declarations;

public sealed partial class DeclarationCollector
{
	private static bool IsExtensionName(string name) => name.Contains('.', StringComparison.Ordinal);

	private void CollectExtensionFunction(FunctionDeclaration declaration)
	{
		var dot = declaration.Name.LastIndexOf('.');
		if (dot <= 0)
			return;

		var receiverName = declaration.Name[..dot];
		var memberName = declaration.Name[(dot + 1)..];
		var receiverSyntax = declaration.ExtensionReceiver
			?? NamedTypeSyntax.Plain(receiverName, declaration.Span);
		var (genericParameters, scope) = BuildExtensionScope(
			receiverSyntax,
			declaration.GenericParameters,
			$"расширения '{declaration.Name}'");
		var receiver = _resolver.Resolve(receiverSyntax, scope);
		if (receiver is ErrorType)
			return;

		var function = new FunctionSymbol(memberName, FunctionKind.Method)
		{
			GenericParameters = genericParameters,
			Parameters = ResolveParameters(declaration.Parameters, scope),
			ReturnType = ResolveReturnType(declaration.ReturnType, scope),
			ReceiverType = receiver,
			Accessibility = declaration.Accessibility,
			IsAbstract = declaration.IsAbstract,
			IsOpen = declaration.IsAbstract || declaration.IsOpen,
			Body = declaration.Body,
			Declaration = declaration,
			Attributes = AttributeValues.Build(declaration.DeclaredAttributes)
		};
		AddExtension(receiver, function);
	}

	private void CollectExtensionProperty(ExtensionPropertyDeclaration declaration)
	{
		var (genericParameters, scope) = BuildExtensionScope(
			declaration.ReceiverType,
			[],
			$"свойства-расширения '{declaration.Name}'");
		var receiver = _resolver.Resolve(declaration.ReceiverType, scope);
		if (receiver is ErrorType)
			return;

		var type = _resolver.Resolve(declaration.PropertyType, scope);
		var getter = new FunctionSymbol(declaration.Name, FunctionKind.Getter)
		{
			GenericParameters = genericParameters,
			ReturnType = type,
			ReceiverType = receiver,
			Accessibility = declaration.Accessibility,
			Body = declaration.Body,
			Declaration = declaration,
			Attributes = AttributeValues.Build(declaration.DeclaredAttributes)
		};
		var property = new PropertySymbol(
			declaration.Name,
			type,
			getter,
			null,
			null,
			false,
			null,
			declaration.Accessibility,
			false,
			false,
			false,
			declaration.Body,
			null)
		{
			IsExtension = true,
			ReceiverType = receiver,
			Declaration = declaration,
			Attributes = AttributeValues.Build(declaration.DeclaredAttributes)
		};
		AddExtension(receiver, property);
	}

	private void CollectExtensionOperator(ExtensionOperatorDeclaration declaration)
	{
		var (genericParameters, scope) = BuildExtensionScope(
			declaration.ReceiverType,
			[],
			$"оператора-расширения '{declaration.Operator}'");
		var receiver = _resolver.Resolve(declaration.ReceiverType, scope);
		if (receiver is ErrorType)
			return;

		var function = new FunctionSymbol(declaration.Operator, FunctionKind.Operator)
		{
			GenericParameters = genericParameters,
			Parameters = ResolveParameters(declaration.Parameters, scope),
			ReturnType = ResolveReturnType(declaration.ReturnType, scope),
			ReceiverType = receiver,
			Accessibility = declaration.Accessibility,
			OpSymbol = declaration.Operator,
			Body = declaration.Body,
			Declaration = declaration,
			Attributes = AttributeValues.Build(declaration.DeclaredAttributes)
		};
		AddExtension(receiver, function);
	}

	private void CollectExtensionOperatorMember(OperatorMemberNode declaration, TypeSymbol owner)
	{
		var dot = declaration.Operator.IndexOf('.', StringComparison.Ordinal);
		if (dot <= 0)
			return;

		var receiverName = declaration.Operator[..dot];
		var symbol = declaration.Operator[(dot + 1)..];
		var scope = _typeScopes.TryGetValue(owner, out var classScope)
			? classScope
			: TypeParameterScope.Empty;
		var receiver = _resolver.Resolve(NamedTypeSyntax.Plain(receiverName, declaration.Span), scope);
		if (receiver is ErrorType)
			return;

		var function = new FunctionSymbol(symbol, FunctionKind.Operator)
		{
			Parameters = ResolveParameters(declaration.Parameters, scope),
			ReturnType = ResolveReturnType(declaration.ReturnType, scope),
			ReceiverType = receiver,
			Accessibility = declaration.Accessibility,
			IsOverride = declaration.IsOverride,
			OpSymbol = symbol,
			Body = declaration.Body,
			Declaration = declaration,
			Attributes = AttributeValues.Build(declaration.DeclaredAttributes)
		};
		AddExtension(receiver, function);
	}

	/// <summary>
	/// Область generic-параметров расширения (spec §10): свободные переменные приёмника
	/// (<c>fun list&lt;T&gt;.first_or(...)</c>) становятся неявными параметрами и идут
	/// перед объявленными <c>&lt;...&gt;</c>, затем в этой области разрешаются приёмник,
	/// параметры и результат.
	/// </summary>
	private (IReadOnlyList<TypeParameterType> Parameters, TypeParameterScope Scope) BuildExtensionScope(
		TypeSyntax? receiverSyntax,
		IReadOnlyList<GenericParameterNode> declared,
		string ownerDescription)
	{
		var implicitParameters = new List<TypeParameterType>();
		var scope = TypeParameterScope.Empty;
		var declaredNames = new HashSet<string>(
			declared.Select(parameter => parameter.Name),
			StringComparer.Ordinal);
		foreach (var name in FreeTypeVariables(receiverSyntax, declaredNames))
		{
			var parameter = new TypeParameterType(name, []);
			scope = scope.Add(parameter);
			implicitParameters.Add(parameter);
		}

		var seen = new HashSet<string>(
			implicitParameters.Select(parameter => parameter.Name),
			StringComparer.Ordinal);
		var (declaredParameters, combined) = BuildFunctionScope(declared, scope, ownerDescription, seen);
		return ([.. implicitParameters, .. declaredParameters], combined);
	}

	/// <summary>
	/// Свободные имена в аргументах типа-приёмника, не являющиеся известными типами:
	/// <c>list&lt;T&gt;</c> → <c>T</c>, <c>dict&lt;K, V&gt;</c> → <c>K</c>, <c>V</c>.
	/// Голова приёмника (<c>list</c>, <c>Box</c>) обязана быть настоящим типом.
	/// </summary>
	private List<string> FreeTypeVariables(TypeSyntax? syntax, HashSet<string> declaredNames)
	{
		var result = new List<string>();
		var seen = new HashSet<string>(StringComparer.Ordinal);
		Collect(syntax);
		return result;

		void Collect(TypeSyntax? node)
		{
			switch (node)
			{
				case NamedTypeSyntax named when named.Arguments.Count > 0:
					foreach (var argument in named.Arguments)
						Collect(argument);
					break;

				case NamedTypeSyntax named:
					if (IsKnownTypeName(named.Name) || declaredNames.Contains(named.Name))
						break;
					if (seen.Add(named.Name))
						result.Add(named.Name);
					break;

				case FunctionTypeSyntax function:
					foreach (var parameter in function.Parameters)
						Collect(parameter);
					Collect(function.ReturnType);
					break;

				case TupleTypeSyntax tuple:
					foreach (var element in tuple.Elements)
						Collect(element.Type);
					break;

				case UnionTypeSyntax union:
					foreach (var alternative in union.Alternatives)
						Collect(alternative);
					break;
			}
		}
	}

	private void AddExtension(KernType receiver, FunctionSymbol function)
	{
		var key = receiver.ToString();
		if (!_extensions.TryGetValue(key, out var builder))
			_extensions.Add(key, builder = new ExtensionBuilder(receiver));
		builder.Functions.Add(function);
	}

	private void AddExtension(KernType receiver, PropertySymbol property)
	{
		var key = receiver.ToString();
		if (!_extensions.TryGetValue(key, out var builder))
			_extensions.Add(key, builder = new ExtensionBuilder(receiver));
		builder.Properties.Add(property);
	}

	private Dictionary<string, ExtensionGroup> BuildExtensions()
	{
		var result = new Dictionary<string, ExtensionGroup>(StringComparer.Ordinal);
		foreach (var (key, builder) in _extensions)
		{
			CheckExtensionConflicts(builder);
			result.Add(key, new ExtensionGroup(builder.Receiver, DistinctSignatures(builder.Functions), [.. builder.Properties]));
		}
		return result;
	}

	private void CheckExtensionConflicts(ExtensionBuilder builder)
	{
		var names = new Dictionary<string, TextSpan>(StringComparer.Ordinal);
		void Register(string name, TextSpan span)
		{
			if (names.TryAdd(name, span))
				return;
			Report(
				"KRN1407",
				$"Повторное имя члена '{name}' у расширения типа '{builder.Receiver}'",
				span);
		}

		var functionNames = new HashSet<string>(StringComparer.Ordinal);
		foreach (var function in builder.Functions)
			if (functionNames.Add(function.Name))
				Register(function.Name, function.Declaration?.Span ?? default);
		foreach (var property in builder.Properties)
			Register(property.Name, property.Declaration?.Span ?? default);

		var signatures = new Dictionary<string, FunctionSymbol>(StringComparer.Ordinal);
		foreach (var function in builder.Functions)
		{
			if (signatures.TryAdd(function.Signature, function))
				continue;
			Report(
				"KRN1400",
				$"Повторная сигнатура функции '{function.Name}' у расширения типа '{builder.Receiver}'",
				function.Declaration?.Span ?? default);
		}
	}

	private sealed class ExtensionBuilder(KernType receiver)
	{
		public KernType Receiver { get; } = receiver;

		public List<FunctionSymbol> Functions { get; } = [];

		public List<PropertySymbol> Properties { get; } = [];
	}
}
