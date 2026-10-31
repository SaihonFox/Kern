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

			foreach (var field in type.Members.Fields)
			{
				if (field.IsStatic)
				{
					DefineStaticField(field);
					continue;
				}
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

			foreach (var property in type.Members.Properties)
			{
				if (property.BackingField is not { } backing)
					continue;

				// Статическое автосвойство живёт в модульном FieldPool, а не в экземпляре.
				if (backing.IsStatic)
				{
					DefineStaticField(backing);
					continue;
				}

				var slot = definition.Fields.Count;
				definition.Fields.Add(new FieldDef
				{
					Name = backing.Name,
					Type = TypeSigMap.From(backing.Type, _module.Name),
					Slot = slot,
					Flags = MemberFlags.None
				});
				_fieldSlots[backing] = slot;
			}

			_typeDefs[type] = definition;
			_typeIndices[type] = _module.AddType(definition);
		}
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
