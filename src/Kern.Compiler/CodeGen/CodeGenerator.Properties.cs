using Kern.Bytecode;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;

namespace Kern.Compiler.CodeGen;

/// <summary>
/// Свойства в <see cref="TypeDef.Properties"/> (spec §4/§16): имя, тип, флаги, индексы
/// аксессоров и backing-поля, атрибуты. Автосвойство не имеет связанных тел accessor'ов —
/// для него синтезируются служебные функции «прочитать/записать backing-поле», чтобы
/// <see cref="PropertyDef.Getter"/>/<see cref="PropertyDef.Setter"/> были настоящими
/// индексами пула функций (рефлексия и линковка не должны зависеть от того, есть ли
/// у свойства ручное тело). Binder по-прежнему обращается к автосвойству как к полю.
/// </summary>
public sealed partial class CodeGenerator
{
	/// <summary>
	/// Заполняет свойства типов после <see cref="Define"/>: индексы аксессоров известны
	/// только после того, как все функции попали в пул.
	/// </summary>
	private void DefineProperties(BoundModule module)
	{
		foreach (var type in module.Symbol.Types.Values)
		{
			if (!_typeDefs.TryGetValue(type, out var definition))
				continue;

			foreach (var property in type.Members.OwnProperties)
				definition.Properties.Add(CreateProperty(property, type, _typeIndices[type]));
		}
	}

	private PropertyDef CreateProperty(PropertySymbol property, TypeSymbol owner, int ownerIndex) => new()
	{
		Name = property.Name,
		Type = TypeSigMap.From(property.Type, _module.Name),
		Flags = PropertyFlags(property),
		OwnerType = ownerIndex,
		Getter = AccessorIndex(property, property.Getter, owner, ownerIndex, FunctionKind.Getter),
		Setter = AccessorIndex(property, property.Setter, owner, ownerIndex, FunctionKind.Setter),
		Init = AccessorIndex(property, property.Init, owner, ownerIndex, FunctionKind.Init),
		BackingField = property.BackingField is { } backing ? FieldSlot(backing) : -1,
		Attributes = [.. property.Attributes],
	};

	private static MemberFlags PropertyFlags(PropertySymbol property)
	{
		var flags = property.Accessibility switch
		{
			Accessibility.Public => MemberFlags.Pub,
			Accessibility.Private => MemberFlags.Priv,
			_ => MemberFlags.None,
		};
		if (property.IsStatic)
			flags |= MemberFlags.Static;
		if (property.Setter is null && property.Init is null)
			flags |= MemberFlags.Readonly;
		if (property.IsAbstract)
			flags |= MemberFlags.Abstract;
		if (property.IsOverride)
			flags |= MemberFlags.Override;
		return flags;
	}

	/// <summary>
	/// Индекс аксессора в пуле функций. У ручного тела он уже есть после <see cref="Define"/>;
	/// у автосвойства тело отсутствует — создаётся пара инструкций над backing-полем.
	/// </summary>
	private int AccessorIndex(
		PropertySymbol property,
		FunctionSymbol? accessor,
		TypeSymbol owner,
		int ownerIndex,
		FunctionKind kind)
	{
		if (accessor is null)
			return -1;
		if (accessor.Body is not null)
			return _functionIndices.TryGetValue(accessor, out var known) ? known : -1;
		if (property.BackingField is null)
			return -1;
		if (_functionIndices.TryGetValue(accessor, out var existing))
			return existing;

		var index = AddSyntheticAccessor(property, owner, ownerIndex, kind);
		_functionIndices[accessor] = index;
		return index;
	}

	private int AddSyntheticAccessor(PropertySymbol property, TypeSymbol owner, int ownerIndex, FunctionKind kind)
	{
		var isGetter = kind == FunctionKind.Getter;
		var isStatic = property.IsStatic;
		var slot = FieldSlot(property.BackingField!);
		if (slot < 0)
			return -1;

		var parameters = new List<TypeSig>();
		if (!isStatic)
			parameters.Add(SelfSignature(owner));
		if (!isGetter)
			parameters.Add(TypeSigMap.From(property.Type, _module.Name));

		var code = new List<int>();
		void Append(OpCode op, int operand)
		{
			code.Add((int)op);
			code.Add(operand);
		}

		void AppendCopyIfUnit()
		{
			if (property.Type is UnitType)
				code.Add((int)OpCode.CopyUnit);
		}

		if (isStatic)
		{
			if (isGetter)
			{
				Append(OpCode.LoadStatic, slot);
				AppendCopyIfUnit();
			}
			else
			{
				Append(OpCode.LoadLocal, 0);
				AppendCopyIfUnit();
				Append(OpCode.StoreStatic, slot);
				code.Add((int)OpCode.PushVoid);
			}
		}
		else
		{
			Append(OpCode.LoadLocal, 0);
			if (isGetter)
			{
				Append(OpCode.LoadField, slot);
				AppendCopyIfUnit();
			}
			else
			{
				Append(OpCode.LoadLocal, 1);
				AppendCopyIfUnit();
				Append(OpCode.StoreField, slot);
				code.Add((int)OpCode.PushVoid);
			}
		}

		code.Add((int)OpCode.Ret);

		var flags = kind switch
		{
			FunctionKind.Getter => MemberFlags.Getter,
			FunctionKind.Setter => MemberFlags.Setter,
			_ => MemberFlags.Init,
		};
		if (isStatic)
			flags |= MemberFlags.Static;

		var definition = new FunctionDef
		{
			Name = property.Name,
			OwnerType = ownerIndex,
			Flags = flags,
			ParamTypes = [.. parameters],
			ReturnType = isGetter ? TypeSigMap.From(property.Type, _module.Name) : TypeSig.Void,
			LocalCount = parameters.Count,
			MaxStack = 4,
			Code = [.. code],
			Attributes = [.. property.Attributes],
		};
		return _module.AddFunction(definition);
	}

	/// <summary>Слот backing-поля: экземплярный — в раскладке типа, статический — в пуле полей модуля.</summary>
	private int FieldSlot(FieldSymbol backing) =>
		_staticSlots.TryGetValue(backing, out var staticSlot)
			? staticSlot
			: _fieldSlots.TryGetValue(backing, out var instanceSlot)
				? instanceSlot
				: -1;

	/// <summary>Подпись экземпляра типа для синтезированного аксессора (с параметрами дженерика).</summary>
	private TypeSig SelfSignature(TypeSymbol owner)
	{
		KernType self = owner.IsValueType
			? new UnitType(owner, owner.GenericParameters, owner.IsImmutable)
			: new ClassType(owner, owner.GenericParameters);
		return TypeSigMap.From(self, _module.Name);
	}
}
