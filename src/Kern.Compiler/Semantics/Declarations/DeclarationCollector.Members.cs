using Kern.Compiler.Semantics.Attributes;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Core.Diagnostics;

namespace Kern.Compiler.Semantics.Declarations;

public sealed partial class DeclarationCollector
{
	private void CollectTypeMembers(TypeDeclaration declaration) =>
		CollectMembers(declaration, declaration.DeclaredMembers, declaration.PrimaryParameters);

	private void CollectUnitMembers(UnitDeclaration declaration) =>
		CollectMembers(declaration, declaration.DeclaredMembers, declaration.PrimaryParameters);

	private void CollectMembers(
		DeclarationNode declaration,
		IReadOnlyList<SyntaxNode> memberNodes,
		IReadOnlyList<ParameterNode> primaryParameters)
	{
		if (!_types.TryGetValue(GetDeclaredName(declaration), out var owner))
			return;

		var scope = _typeScopes.TryGetValue(owner, out var existing)
			? existing
			: TypeParameterScope.Empty;
		var fields = new List<FieldSymbol>();
		var properties = new List<PropertySymbol>();

		// Сначала поля и свойства: параметрам `$name` нужны уже объявленные поля.
		foreach (var node in memberNodes)
		{
			switch (node)
			{
				case FieldMemberNode field:
					fields.Add(CollectField(field, owner, scope));
					break;
				case PropertyMemberNode property:
					properties.Add(CollectProperty(property, owner, scope));
					break;
			}
		}

		var constructors = new List<FunctionSymbol>();
		if (primaryParameters.Count > 0)
			constructors.Add(CollectPrimaryConstructor(declaration, owner, primaryParameters, scope, fields));

		var methods = new List<FunctionSymbol>();
		var operators = new List<FunctionSymbol>();
		var parentSelections = new List<FunctionSymbol>();
		var parentFieldSelections = new List<FieldSymbol>();

		foreach (var node in memberNodes)
		{
			switch (node)
			{
				case FunctionDeclaration function when IsExtensionName(function.Name):
					CollectExtensionFunction(function);
					break;
				case FunctionDeclaration function:
					methods.Add(CollectMethod(function, owner, scope, fields));
					break;
				case ConstructorMemberNode constructor:
					constructors.Add(CollectConstructor(constructor, owner, scope, fields));
					break;
				case OperatorMemberNode @operator when IsExtensionName(@operator.Operator):
					CollectExtensionOperatorMember(@operator, owner);
					break;
				case OperatorMemberNode @operator:
					operators.Add(CollectOperator(@operator, owner, scope));
					break;
				case ParentMethodSelectionNode selection:
					parentSelections.Add(CollectParentMethodSelection(selection, owner, scope));
					break;
				case ParentFieldSelectionNode selection:
					parentFieldSelections.Add(CollectParentFieldSelection(selection, owner, scope));
					break;
			}
		}

		// Конструктор по умолчанию синтезируется только при полном отсутствии других.
		// Абстрактный `@type` не конструируется (spec §8): нет конструктора — `KRN2103`.
		if (constructors.Count == 0 && !owner.IsStatic && !owner.IsAbstract)
		{
			constructors.Add(new FunctionSymbol("ctor", FunctionKind.Constructor)
			{
				Owner = owner,
				// Тип-параметры владельца нужны инициализаторам полей: `<T>[]` в `Stack<T>`.
				GenericParameters = [.. owner.GenericParameters],
				Accessibility = owner.Accessibility,
				Declaration = declaration
			});
		}

		CheckMemberConflicts(owner, fields, properties, methods, parentSelections, operators, constructors);

		var staticInitializer = HasStaticInitializers(fields, properties)
			? new FunctionSymbol("<static-init>", FunctionKind.Function)
			{
				Owner = owner,
				IsStatic = true,
				Accessibility = Accessibility.Private,
				Declaration = declaration
			}
			: null;

		owner.Members = MemberTable.Create(
			GroupMethods(methods),
			DistinctFields(fields),
			DistinctProperties(properties),
			DistinctSignatures(constructors),
			DistinctSignatures(operators),
			DistinctSignatures(parentSelections),
			parentFieldSelections,
			staticInitializer);

		// Slice A3a: экземплярные члены всех прямых родителей-классов видны в типе. Таблица
		// содержит символы родителей и читает их лениво, поэтому порядок разрешения не важен.
		var directBases = Inheritance.DirectBases(owner);
		if (directBases.Count > 0)
			owner.Members.SetInheritedBases([.. directBases.Select(baseType => baseType.Symbol)]);
	}

	private static string GetDeclaredName(DeclarationNode declaration) => declaration switch
	{
		TypeDeclaration type => type.Name,
		UnitDeclaration unit => unit.Name,
		_ => ""
	};

	private FieldSymbol CollectField(FieldMemberNode node, TypeSymbol owner, TypeParameterScope scope) =>
		new(
			node.Name,
			_resolver.Resolve(node.Type, scope),
			node.IsRequired || node.Type is NamedTypeSyntax { IsSingleAssignment: true },
			node.IsStatic,
			node.Accessibility,
			node.Initializer,
			false)
		{
			Owner = owner,
			Declaration = node,
			Attributes = AttributeValues.Build(node.DeclaredAttributes)
		};

	private PropertySymbol CollectProperty(PropertyMemberNode node, TypeSymbol owner, TypeParameterScope scope)
	{
		var type = _resolver.Resolve(node.Type, scope);
		var getter = FindAccessor(node, AccessorKind.Get, type, owner, scope);
		var setter = FindAccessor(node, AccessorKind.Set, type, owner, scope);
		var init = FindAccessor(node, AccessorKind.Init, type, owner, scope);

		// `i32 b => expr` — get-only свойство с expression body и без backing-поля.
		if (getter is null && node.ExpressionBody is not null)
		{
			getter = new FunctionSymbol(node.Name, FunctionKind.Getter)
			{
				Owner = owner,
				ReturnType = type,
				Accessibility = node.Accessibility,
				IsStatic = node.IsStatic,
				IsOverride = node.IsOverride,
				Body = node.ExpressionBody,
				Declaration = node,
				Attributes = AttributeValues.Build(node.DeclaredAttributes)
			};
		}

		var backingField = node.IsAuto
			? new FieldSymbol($"_{node.Name}", type, false, node.IsStatic, Accessibility.Private, node.Initializer, false)
			{
				Owner = owner,
				IsBackingField = true,
				Declaration = node
			}
			: null;

		return new PropertySymbol(
			node.Name,
			type,
			getter,
			setter,
			init,
			node.IsAuto,
			backingField,
			node.Accessibility,
			node.IsStatic,
			node.IsOverride,
			false,
			node.ExpressionBody,
			node.Initializer)
		{
			Owner = owner,
			Declaration = node,
			Attributes = AttributeValues.Build(node.DeclaredAttributes)
		};
	}

	private FunctionSymbol? FindAccessor(
		PropertyMemberNode node,
		AccessorKind kind,
		KernType propertyType,
		TypeSymbol owner,
		TypeParameterScope scope)
	{
		var accessor = node.Accessors.FirstOrDefault(item => item.Kind == kind);
		return accessor is null ? null : CreateAccessor(accessor, node, propertyType, owner, scope);
	}

	private FunctionSymbol CreateAccessor(
		PropertyAccessorNode accessor,
		PropertyMemberNode property,
		KernType propertyType,
		TypeSymbol owner,
		TypeParameterScope scope)
	{
		var kind = accessor.Kind switch
		{
			AccessorKind.Get => FunctionKind.Getter,
			AccessorKind.Set => FunctionKind.Setter,
			_ => FunctionKind.Init
		};
		IReadOnlyList<ParameterSymbol> parameters = [];
		if (kind is FunctionKind.Setter or FunctionKind.Init)
		{
			// У `set(value)` тип параметра не пишется: он всегда равен типу свойства.
			// `init` получает такой же неявный `value`: объявлять параметры ему нельзя
			// (spec §8), но запись `$.p = v` передаёт значение в тело accessor'а.
			if (accessor.Parameters.Count > 0)
			{
				var node = accessor.Parameters[0];
				var parameterType = node.Type is null ? propertyType : _resolver.Resolve(node.Type, scope);
				parameters =
				[
					new ParameterSymbol(node.Name, parameterType, node.DefaultValue, node.IsAutoInit, false, node.Span)
				];
			}
			else
			{
				parameters = [new ParameterSymbol("value", propertyType, null, false, false, accessor.Span)];
			}
		}

		return new FunctionSymbol(property.Name, kind)
		{
			Owner = owner,
			Parameters = parameters,
			ReturnType = kind == FunctionKind.Getter ? propertyType : null,
			Accessibility = property.Accessibility,
			IsStatic = property.IsStatic,
			IsOverride = property.IsOverride,
			Body = accessor.Body,
			Declaration = property,
			Attributes = AttributeValues.Build(property.DeclaredAttributes)
		};
	}

	private FunctionSymbol CollectMethod(
		FunctionDeclaration declaration,
		TypeSymbol owner,
		TypeParameterScope classScope,
		List<FieldSymbol> fields)
	{
		var (genericParameters, scope) = BuildFunctionScope(
			declaration.GenericParameters,
			classScope,
			$"метода '{owner.Name}.{declaration.Name}'");
		var parameters = ResolveParameters(declaration.Parameters, scope);
		LinkAutoInitParameters(parameters, fields, owner);

		return new FunctionSymbol(declaration.Name, FunctionKind.Method)
		{
			GenericParameters = genericParameters,
			Parameters = parameters,
			ReturnType = ResolveReturnType(declaration.ReturnType, scope),
			Owner = owner,
			Accessibility = declaration.Accessibility,
			IsStatic = declaration.IsStatic,
			IsAbstract = declaration.IsAbstract,
			IsOpen = declaration.IsAbstract,
			IsOverride = declaration.IsOverride,
			Body = declaration.Body,
			Declaration = declaration,
			Attributes = AttributeValues.Build(declaration.DeclaredAttributes)
		};
	}

	private void CollectTopLevelFunction(FunctionDeclaration declaration)
	{
		if (IsExtensionName(declaration.Name))
		{
			CollectExtensionFunction(declaration);
			return;
		}

		var (genericParameters, scope) = BuildFunctionScope(
			declaration.GenericParameters,
			TypeParameterScope.Empty,
			$"функции '{declaration.Name}'");
		var parameters = ResolveParameters(declaration.Parameters, scope);
		var function = new FunctionSymbol(declaration.Name, FunctionKind.Function)
		{
			GenericParameters = genericParameters,
			Parameters = parameters,
			ReturnType = ResolveReturnType(declaration.ReturnType, scope),
			Accessibility = declaration.Accessibility,
			IsAbstract = declaration.IsAbstract,
			IsOpen = declaration.IsAbstract,
			IsOverride = declaration.IsOverride,
			Body = declaration.Body,
			Declaration = declaration,
			Attributes = AttributeValues.Build(declaration.DeclaredAttributes)
		};

		if (!_functions.TryGetValue(declaration.Name, out var group))
			_functions.Add(declaration.Name, group = []);
		group.Add(function);
	}

	private FunctionSymbol CollectConstructor(
		ConstructorMemberNode declaration,
		TypeSymbol owner,
		TypeParameterScope scope,
		List<FieldSymbol> fields)
	{
		var parameters = ResolveParameters(declaration.Parameters, scope);
		LinkAutoInitParameters(parameters, fields, owner);
		return new FunctionSymbol("ctor", FunctionKind.Constructor)
		{
			Parameters = parameters,
			Owner = owner,
			Accessibility = declaration.Accessibility,
			IsOverride = declaration.IsOverride,
			Chain = declaration.Chain,
			Body = declaration.Body,
			Declaration = declaration,
			Attributes = AttributeValues.Build(declaration.DeclaredAttributes)
		};
	}

	private FunctionSymbol CollectPrimaryConstructor(
		DeclarationNode declaration,
		TypeSymbol owner,
		IReadOnlyList<ParameterNode> parameters,
		TypeParameterScope scope,
		List<FieldSymbol> fields)
	{
		var resolved = ResolveParameters(parameters, scope);
		LinkPrimaryParameters(resolved, fields, owner);
		var constructor = new FunctionSymbol("ctor", FunctionKind.Constructor)
		{
			Parameters = resolved,
			Owner = owner,
			Accessibility = owner.Accessibility,
			IsPrimaryConstructor = true,
			Declaration = declaration
		};
		owner.PrimaryConstructor = constructor;
		return constructor;
	}

	private FunctionSymbol CollectOperator(OperatorMemberNode declaration, TypeSymbol owner, TypeParameterScope scope) =>
		new(declaration.Operator, FunctionKind.Operator)
		{
			Parameters = ResolveParameters(declaration.Parameters, scope),
			ReturnType = ResolveReturnType(declaration.ReturnType, scope),
			Owner = owner,
			Accessibility = declaration.Accessibility,
			IsOverride = declaration.IsOverride,
			OpSymbol = declaration.Operator,
			Body = declaration.Body,
			Declaration = declaration,
			Attributes = AttributeValues.Build(declaration.DeclaredAttributes)
		};

	private FunctionSymbol CollectParentMethodSelection(
		ParentMethodSelectionNode declaration,
		TypeSymbol owner,
		TypeParameterScope scope) =>
		new(declaration.Name, FunctionKind.ParentSelection)
		{
			Parameters = ResolveParameters(declaration.Parameters, scope),
			ReturnType = ResolveReturnType(declaration.ReturnType, scope),
			Owner = owner,
			Accessibility = declaration.Accessibility,
			IsOverride = declaration.IsOverride,
			ParentSelector = declaration.ParentType,
			Declaration = declaration,
			Attributes = AttributeValues.Build(declaration.DeclaredAttributes)
		};

	private FieldSymbol CollectParentFieldSelection(
		ParentFieldSelectionNode declaration,
		TypeSymbol owner,
		TypeParameterScope scope) =>
		new(
			declaration.Name,
			_resolver.Resolve(declaration.Type, scope),
			declaration.Type is NamedTypeSyntax { IsSingleAssignment: true },
			false,
			declaration.Accessibility,
			null,
			false)
		{
			Owner = owner,
			ParentSelector = declaration.ParentType,
			ParentSelectorType = declaration.ParentType,
			Declaration = declaration,
			Attributes = AttributeValues.Build(declaration.DeclaredAttributes)
		};

	private List<ParameterSymbol> ResolveParameters(
		IReadOnlyList<ParameterNode> nodes,
		TypeParameterScope scope)
	{
		var parameters = new List<ParameterSymbol>(nodes.Count);
		foreach (var node in nodes)
		{
			var type = node.Type is null
				? ErrorType.Instance
				: _resolver.Resolve(node.Type, scope);
			parameters.Add(new ParameterSymbol(
				node.Name,
				type,
				node.DefaultValue,
				node.IsAutoInit,
				false,
				node.Span));
		}
		return parameters;
	}

	/// <summary>
	/// Параметры primary-конструктора создают одноимённые поля и инициализируют их (spec §8, §9).
	/// Если поле уже объявлено в теле типа, параметр связывается с ним. Видимость созданных
	/// полей — <c>pub</c> (см. DECISIONS_NEEDED Q11).
	/// </summary>
	private static void LinkPrimaryParameters(List<ParameterSymbol> parameters, List<FieldSymbol> fields, TypeSymbol owner)
	{
		for (var index = 0; index < parameters.Count; index++)
		{
			var parameter = parameters[index];
			var field = fields.FirstOrDefault(candidate => candidate.Name == parameter.Name);
			if (field is null)
			{
				field = new FieldSymbol(
					parameter.Name,
					parameter.Type,
					false,
					false,
					Accessibility.Public,
					null,
					false)
				{
					Owner = owner,
					IsAutoInit = true
				};
				fields.Add(field);
			}
			parameters[index] = parameter with { Field = field };
		}
	}

	/// <summary>
	/// Параметр с <c>$</c> связывается с одноимённым объявленным полем; если поля нет,
	/// создаётся приватное поле с типом параметра.
	/// </summary>
	private static void LinkAutoInitParameters(List<ParameterSymbol> parameters, List<FieldSymbol> fields, TypeSymbol owner)
	{
		for (var index = 0; index < parameters.Count; index++)
		{
			var parameter = parameters[index];
			if (!parameter.IsAutoInit)
				continue;

			var field = fields.FirstOrDefault(candidate => candidate.Name == parameter.Name);
			if (field is null)
			{
				if (parameter.Type is ErrorType)
					continue;
				field = new FieldSymbol(
					parameter.Name,
					parameter.Type,
					false,
					false,
					Accessibility.Private,
					null,
					false)
				{
					Owner = owner,
					IsAutoInit = true
				};
				fields.Add(field);
			}
			parameters[index] = parameter with { Field = field };
		}
	}

	private KernType? ResolveReturnType(TypeSyntax? syntax, TypeParameterScope scope) =>
		syntax is null ? null : _resolver.Resolve(syntax, scope);

	private static bool HasStaticInitializers(
		IReadOnlyList<FieldSymbol> fields,
		IReadOnlyList<PropertySymbol> properties) =>
		fields.Any(field => field.IsStatic && field.Initializer is not null) ||
		properties.Any(property => property.IsStatic && property.Initializer is not null);

	private static Dictionary<string, List<FunctionSymbol>> GroupMethods(IReadOnlyList<FunctionSymbol> methods)
	{
		var groups = new Dictionary<string, List<FunctionSymbol>>(StringComparer.Ordinal);
		foreach (var method in methods)
		{
			if (!groups.TryGetValue(method.Name, out var group))
				groups.Add(method.Name, group = []);
			if (group.Any(existing => existing.Signature == method.Signature))
				continue;
			group.Add(method);
		}
		return groups;
	}

	/// <summary>Оставляет первое поле каждого имени: повторы уже отмечены KRN1407.</summary>
	private static IReadOnlyList<FieldSymbol> DistinctFields(IReadOnlyList<FieldSymbol> fields)
	{
		var names = new HashSet<string>(StringComparer.Ordinal);
		var result = new List<FieldSymbol>(fields.Count);
		foreach (var field in fields)
			if (names.Add(field.Name))
				result.Add(field);
		return result;
	}

	/// <summary>Оставляет первое свойство каждого имени: повторы уже отмечены KRN1407.</summary>
	private static IReadOnlyList<PropertySymbol> DistinctProperties(IReadOnlyList<PropertySymbol> properties)
	{
		var names = new HashSet<string>(StringComparer.Ordinal);
		var result = new List<PropertySymbol>(properties.Count);
		foreach (var property in properties)
			if (names.Add(property.Name))
				result.Add(property);
		return result;
	}

	/// <summary>Оставляет первую функцию каждой сигнатуры: повторы уже отмечены KRN1400.</summary>
	private static IReadOnlyList<FunctionSymbol> DistinctSignatures(IReadOnlyList<FunctionSymbol> functions)
	{
		var seen = new HashSet<string>(StringComparer.Ordinal);
		var result = new List<FunctionSymbol>(functions.Count);
		foreach (var function in functions)
			if (seen.Add(function.Signature))
				result.Add(function);
		return result;
	}
}
