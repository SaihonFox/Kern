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

	/// <summary>Следующий модульный слот экземплярного поля класса (см. <see cref="ClassFieldSlot"/>).</summary>
	private int _nextClassFieldSlot;

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
			definition.Attributes = [.. type.Attributes];

			// Собственные статические поля и статические backing-поля живут в модульном пуле,
			// унаследованные статические члены не наследуются (spec §8).
			foreach (var field in type.Members.OwnFields)
				if (field.IsStatic)
					DefineStaticField(field);

			foreach (var property in type.Members.OwnProperties)
				if (property.BackingField is { IsStatic: true } staticBacking)
					DefineStaticField(staticBacking);

			// Раскладка экземпляра: все предки в топологическом порядке (каждый предок раньше
			// своих потомков, без повторов), затем собственные поля и backing-поля автосвойств.
			// У класса слоты полей модульные (см. ClassFieldSlot): тело метода родителя
			// компилируется один раз и должно находить свои поля в любом наследнике.
			var layout = InstanceLayout(type);
			if (type.IsValueType)
			{
				// unit не наследуется: слоты плотные, по индексу в раскладке.
				foreach (var field in layout)
				{
					var slot = definition.Fields.Count;
					definition.Fields.Add(CreateInstanceField(field, slot));
					_fieldSlots[field] = slot;
				}
			}
			else
			{
				var slots = new int[layout.Count];
				var size = 0;
				for (var index = 0; index < layout.Count; index++)
				{
					slots[index] = ClassFieldSlot(layout[index]);
					if (slots[index] >= size)
						size = slots[index] + 1;
				}

				var bySlot = new FieldDef?[size];
				for (var index = 0; index < layout.Count; index++)
					bySlot[slots[index]] = CreateInstanceField(layout[index], slots[index]);
				for (var slot = 0; slot < size; slot++)
					definition.Fields.Add(bySlot[slot] ?? CreateSlotPadding(slot));
			}

			definition.Parents = [.. type.Bases.Select(baseType => TypeSigMap.From(baseType, _module.Name))];

			_typeDefs[type] = definition;
			_typeIndices[type] = _module.AddType(definition);
		}
	}

	/// <summary>
	/// Порядок слотов экземпляра: все предки в топологическом порядке (см.
	/// <see cref="Inheritance.Ancestors"/>), затем собственные поля и backing-поля автосвойств.
	/// Для одиночного наследования это прежний порядок «родитель → наследник»; при нескольких
	/// родителях их части идут в порядке объявления, общий предок — один раз (ромб отвергается
	/// отдельной диагностикой).
	/// </summary>
	private IReadOnlyList<FieldSymbol> InstanceLayout(TypeSymbol type)
	{
		if (_instanceLayouts.TryGetValue(type, out var cached))
			return cached;

		var layout = new List<FieldSymbol>();
		foreach (var ancestor in Inheritance.Ancestors(type))
			AddOwnFields(layout, ancestor);
		AddOwnFields(layout, type);

		_instanceLayouts[type] = layout;
		return layout;
	}

	private static void AddOwnFields(List<FieldSymbol> layout, TypeSymbol type)
	{
		foreach (var field in type.Members.OwnFields)
			if (!field.IsStatic)
				layout.Add(field);

		foreach (var property in type.Members.OwnProperties)
			if (!property.IsStatic && property.BackingField is { } backing)
				layout.Add(backing);
	}

	private FieldDef CreateInstanceField(FieldSymbol field, int slot) => new()
	{
		Name = field.Name,
		Type = TypeSigMap.From(field.Type, _module.Name),
		Slot = slot,
		Flags = field.IsRequired ? MemberFlags.Readonly : MemberFlags.None,
		Attributes = [.. field.Attributes]
	};

	/// <summary>
	/// Модульный слот экземплярного поля класса. Методы родителей компилируются один раз, поэтому
	/// один и тот же <see cref="FieldSymbol"/> обязан занимать один и тот же индекс в объекте
	/// любого наследника: при нескольких независимых родителях их части не могут начинаться с 0.
	/// Объект выделяет слоты до максимального, промежутки заполняются заглушками
	/// (<see cref="CreateSlotPadding"/>), чтобы <c>TypeDef.Fields.Count</c> оставался размером
	/// массива полей в VM (изменений VM не требуется).
	/// </summary>
	private int ClassFieldSlot(FieldSymbol field)
	{
		if (_fieldSlots.TryGetValue(field, out var existing))
			return existing;

		var slot = _nextClassFieldSlot++;
		_fieldSlots[field] = slot;
		return slot;
	}

	/// <summary>Пустой слот между полями разных родителей: держит размер массива объекта.</summary>
	private static FieldDef CreateSlotPadding(int slot) => new()
	{
		Name = $"$slot{slot}",
		Slot = slot,
		Flags = MemberFlags.Priv | MemberFlags.Padding
	};

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
			ReturnType = function.Symbol.IsAsync
				? TypeSigMap.From(
					new BuiltinGenericType(BuiltinGenericKind.Task, [function.ReturnType]), _module.Name)
				: TypeSigMap.From(function.ReturnType, _module.Name),
			LocalCount = Math.Max(function.LocalCount, function.Parameters.Count),
			MaxStack = 256,
			Flags = function.Symbol.Owner is null ? MemberFlags.Static : MemberFlags.None,
			Attributes = [.. function.Symbol.Attributes]
		};

		if (function.Symbol.Kind == FunctionKind.Constructor)
			definition.Flags |= MemberFlags.Ctor;
		if (function.Symbol.IsStatic)
			definition.Flags |= MemberFlags.Static;
		if (function.Symbol.IsAsync)
			definition.Flags |= MemberFlags.Async;
		if (function.Symbol.Kind == FunctionKind.Getter)
			definition.Flags |= MemberFlags.Getter;
		else if (function.Symbol.Kind == FunctionKind.Setter)
			definition.Flags |= MemberFlags.Setter;
		else if (function.Symbol.Kind == FunctionKind.Init)
			definition.Flags |= MemberFlags.Init;

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
