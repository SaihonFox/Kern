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
		var receiver = _resolver.Resolve(NamedTypeSyntax.Plain(receiverName, declaration.Span), TypeParameterScope.Empty);
		if (receiver is ErrorType)
			return;

		var (genericParameters, scope) = BuildFunctionScope(
			declaration.GenericParameters,
			TypeParameterScope.Empty,
			$"расширения '{declaration.Name}'");
		var function = new FunctionSymbol(memberName, FunctionKind.Method)
		{
			GenericParameters = genericParameters,
			Parameters = ResolveParameters(declaration.Parameters, scope),
			ReturnType = ResolveReturnType(declaration.ReturnType, scope),
			ReceiverType = receiver,
			Accessibility = declaration.Accessibility,
			IsAbstract = declaration.IsAbstract,
			IsOpen = declaration.IsAbstract,
			Body = declaration.Body,
			Declaration = declaration
		};
		AddExtension(receiver, function);
	}

	private void CollectExtensionProperty(ExtensionPropertyDeclaration declaration)
	{
		var receiver = _resolver.Resolve(declaration.ReceiverType, TypeParameterScope.Empty);
		if (receiver is ErrorType)
			return;

		var type = _resolver.Resolve(declaration.PropertyType, TypeParameterScope.Empty);
		var getter = new FunctionSymbol(declaration.Name, FunctionKind.Getter)
		{
			ReturnType = type,
			ReceiverType = receiver,
			Accessibility = declaration.Accessibility,
			Body = declaration.Body,
			Declaration = declaration
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
			Declaration = declaration
		};
		AddExtension(receiver, property);
	}

	private void CollectExtensionOperator(ExtensionOperatorDeclaration declaration)
	{
		var receiver = _resolver.Resolve(declaration.ReceiverType, TypeParameterScope.Empty);
		if (receiver is ErrorType)
			return;

		var function = new FunctionSymbol(declaration.Operator, FunctionKind.Operator)
		{
			Parameters = ResolveParameters(declaration.Parameters, TypeParameterScope.Empty),
			ReturnType = ResolveReturnType(declaration.ReturnType, TypeParameterScope.Empty),
			ReceiverType = receiver,
			Accessibility = declaration.Accessibility,
			OpSymbol = declaration.Operator,
			Body = declaration.Body,
			Declaration = declaration
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
			Declaration = declaration
		};
		AddExtension(receiver, function);
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
