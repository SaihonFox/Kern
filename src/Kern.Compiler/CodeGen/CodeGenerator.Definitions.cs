using Kern.Bytecode;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;

namespace Kern.Compiler.CodeGen;

/// <summary>
/// Определения пулов модуля: типы, их поля/слоты, функции и нативные привязки.
/// Отделено от <c>CodeGenerator.cs</c>, чтобы эмиссия инструкций и описания символов
/// не разрастались в один файл.
/// </summary>
public sealed partial class CodeGenerator
{
	private readonly Dictionary<TypeSymbol, IReadOnlyList<FieldSymbol>> _instanceLayouts =
		new(ReferenceEqualityComparer.Instance);

	/// <summary>Native-функция в байткоде: пустое тело, флаг Native и ключ привязки (spec §12).</summary>
	private void DefineNative(FunctionSymbol symbol)
	{
		if (_functionIndices.ContainsKey(symbol) || symbol.NativeKey is null)
			return;

		var parameterTypes = new List<TypeSig>();
		if (symbol.NativeSelfType is { } self)
			parameterTypes.Add(TypeSigMap.From(self, _module.Name));
		parameterTypes.AddRange(symbol.Parameters.Select(parameter => TypeSigMap.From(parameter.Type, _module.Name)));

		var definition = new FunctionDef
		{
			Name = symbol.Name,
			ParamTypes = [.. parameterTypes],
			ReturnType = TypeSigMap.From(symbol.ReturnType ?? PrimitiveType.Void, _module.Name),
			LocalCount = 0,
			MaxStack = 0,
			Flags = MemberFlags.Native | MemberFlags.Static,
			NativeKey = symbol.NativeKey,
		};
		_functionIndices[symbol] = _module.AddFunction(definition);
	}

	private void DefineTypes(BoundModule module)
	{
		foreach (var type in module.Symbol.Types.Values)
		{
			var definition = new TypeDef
			{
				Name = type.Name,
				FullName = TypeSigMap.FullName(type, _module.Name),
				Kind = type.Kind switch
				{
					SymbolKind.Unit => TypeDefKind.Unit,
					SymbolKind.ImmutableUnit => TypeDefKind.ImmutableUnit,
					_ => TypeDefKind.Class
				},
				GenericParams = [.. type.GenericParameterNames]
			};

			// Собственные статические поля и статические backing-поля живут в модульном пуле,
			// унаследованные статические члены не наследуются (spec §8).
			foreach (var field in type.Members.OwnFields)
				if (field.IsStatic)
					DefineStaticField(field);

			foreach (var property in type.Members.OwnProperties)
				if (property.BackingField is { IsStatic: true } staticBacking)
					DefineStaticField(staticBacking);

			// Раскладка экземпляра: сначала поля родителя (в его порядке), затем собственные
			// поля и backing-поля автосвойств. Слоты родителя в его TypeDef совпадают.
			foreach (var field in InstanceLayout(type))
			{
				var slot = definition.Fields.Count;
				definition.Fields.Add(new FieldDef
				{
					Name = field.Name,
					Type = TypeSigMap.From(field.Type, _module.Name),
					Slot = slot,
					Flags = field.IsRequired ? MemberFlags.Readonly : MemberFlags.None
				});
				_fieldSlots[field] = slot;
			}

			definition.Parents = [.. type.Bases.Select(baseType => TypeSigMap.From(baseType, _module.Name))];

			_typeDefs[type] = definition;
			_typeIndices[type] = _module.AddType(definition);
		}
	}

	/// <summary>
	/// Порядок слотов экземпляра: родительская часть (в порядке её <see cref="TypeDef"/>) →
	/// собственные поля → backing-поля собственных автосвойств. Так методы родителя,
	/// сгенерированные с его слотами, работают и на объекте наследника.
	/// </summary>
	private IReadOnlyList<FieldSymbol> InstanceLayout(TypeSymbol type)
	{
		if (_instanceLayouts.TryGetValue(type, out var cached))
			return cached;

		var layout = new List<FieldSymbol>();
		if (Inheritance.DirectBase(type) is { } baseType)
			layout.AddRange(InstanceLayout(baseType.Symbol));

		foreach (var field in type.Members.OwnFields)
			if (!field.IsStatic)
				layout.Add(field);

		foreach (var property in type.Members.OwnProperties)
			if (!property.IsStatic && property.BackingField is { } backing)
				layout.Add(backing);

		_instanceLayouts[type] = layout;
		return layout;
	}

	/// <summary>
	/// Функция в пуле: имя, подпись, флаги и владелец. Инициализатор типа получает
	/// уникальное имя <c>Type.&lt;static-init&gt;</c> (Q15b), модульный
	/// <c>&lt;static-init&gt;</c> — языковое и индекс <see cref="KbcModule.StaticInit"/>.
	/// </summary>
	private void Define(BoundFunction function)
	{
		var typeInitializer = function.Symbol is { Name: "<static-init>", Owner: not null };
		var definition = new FunctionDef
		{
			Name = typeInitializer
				? $"{function.Symbol.Owner!.Name}.<static-init>"
				: function.Symbol.Name,
			ParamTypes = [.. function.Parameters.Select(parameter => TypeSigMap.From(parameter.Symbol.Type, _module.Name))],
			ReturnType = TypeSigMap.From(function.ReturnType, _module.Name),
			LocalCount = Math.Max(function.LocalCount, function.Parameters.Count),
			MaxStack = 256,
			Flags = function.Symbol.Owner is null ? MemberFlags.Static : MemberFlags.None
		};

		if (function.Symbol.Kind == FunctionKind.Constructor)
			definition.Flags |= MemberFlags.Ctor;
		if (function.Symbol.IsStatic)
			definition.Flags |= MemberFlags.Static;

		var index = _module.AddFunction(definition);
		_functionIndices[function.Symbol] = index;
		if (function.Symbol.Owner is { } owner && _typeIndices.TryGetValue(owner, out var typeIndex))
		{
			definition.OwnerType = typeIndex;
			if (function.Symbol.Kind == FunctionKind.Constructor)
				_typeDefs[owner].Ctors.Add(index);
			else
				_typeDefs[owner].Methods.Add(index);
		}

		if (function.Symbol.Name == "<static-init>" && function.Symbol.Owner is null)
			_module.StaticInit = index;
		else if (function.Symbol.Name == "main" && function.Symbol.Owner is null)
			_module.EntryPoint = index;
	}
}
