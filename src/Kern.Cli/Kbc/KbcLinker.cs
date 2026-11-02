using Kern.Bytecode;

namespace Kern.Cli.Kbc;

/// <summary>
/// Линковка <c>.kbc</c>-библиотек в модуль потребителя (spec round 25): интерфейсные
/// заглушки из исходников заменяются настоящим кодом, операнды переписываются в пулы
/// потребителя, типы сопоставляются по полному имени (или простому имени), раскладка
/// полей проверяется.
/// </summary>
internal static class KbcLinker
{
	public static KbcModule Link(KbcModule target, IReadOnlyList<KbcModule> libraries)
	{
		ArgumentNullException.ThrowIfNull(target);
		ArgumentNullException.ThrowIfNull(libraries);
		foreach (KbcModule library in libraries)
		{
			if (ReferenceEquals(library, target))
				continue;
			new Context(target, library).Link();
			if (!target.References.Contains(library.Name, StringComparer.Ordinal))
				target.References.Add(library.Name);
		}

		return target;
	}

	private sealed class Context(KbcModule target, KbcModule library)
	{
		private readonly Dictionary<int, int> _types = [];
		private readonly Dictionary<int, int> _functions = [];
		private readonly Dictionary<int, int> _globals = [];
		private readonly Dictionary<int, Dictionary<int, int>> _fieldSlots = [];
		private readonly Dictionary<string, string> _typeNames = new(StringComparer.Ordinal);
		private readonly Dictionary<string, string> _simpleTypeNames = new(StringComparer.Ordinal);
		private readonly HashSet<int> _newTypes = [];

		public void Link()
		{
			MapTypes();
			MapFunctions();
			MapGlobals();
			ApplyTypes();
			ApplyFunctions();
			MergeStaticInit();
		}

		private void MapTypes()
		{
			for (var index = 0; index < library.TypePool.Count; index++)
			{
				TypeDef type = library.TypePool[index];
				int existing = FindTargetType(type);
				if (existing >= 0)
				{
					_types[index] = existing;
					_fieldSlots[index] = MapFieldSlots(type, target.TypePool[existing]);
					_typeNames[type.FullName] = target.TypePool[existing].FullName;
					_simpleTypeNames[type.Name] = target.TypePool[existing].FullName;
					continue;
				}

				var copy = new TypeDef
				{
					Name = type.Name,
					FullName = type.FullName,
					Kind = type.Kind,
					Flags = type.Flags,
					GenericParams = [.. type.GenericParams],
				};
				target.TypePool.Add(copy);
				int added = target.TypePool.Count - 1;
				_newTypes.Add(added);
				_types[index] = added;
				_fieldSlots[index] = [];
				_typeNames[type.FullName] = copy.FullName;
				_simpleTypeNames[type.Name] = copy.FullName;
			}
		}

		private int FindTargetType(TypeDef type)
		{
			for (var index = 0; index < target.TypePool.Count; index++)
				if (string.Equals(target.TypePool[index].FullName, type.FullName, StringComparison.Ordinal))
					return index;
			for (var index = 0; index < target.TypePool.Count; index++)
				if (string.Equals(target.TypePool[index].Name, type.Name, StringComparison.Ordinal))
					return index;
			return -1;
		}

		private static Dictionary<int, int> MapFieldSlots(TypeDef libraryType, TypeDef targetType)
		{
			var map = new Dictionary<int, int>();
			var libraryFields = libraryType.Fields.Where(field => !IsPadding(field.Name)).ToList();
			var targetFields = targetType.Fields.Where(field => !IsPadding(field.Name)).ToList();
			if (libraryFields.Count != targetFields.Count)
				throw new Build.BuildException(
					$".kbc: раскладка полей типа '{libraryType.FullName}' не совпадает " +
					$"(библиотека: {libraryFields.Count}, проект: {targetFields.Count})");
			for (var index = 0; index < libraryFields.Count; index++)
			{
				FieldDef source = libraryFields[index];
				FieldDef destination = targetFields.FirstOrDefault(field => field.Name == source.Name)
					?? throw new Build.BuildException(
						$".kbc: у типа '{libraryType.FullName}' нет поля '{source.Name}'");
				if (source.Type.Canonical != destination.Type.Canonical)
					throw new Build.BuildException(
						$".kbc: поле '{libraryType.FullName}.{source.Name}' имеет тип " +
						$"'{source.Type.Canonical}', а в проекте '{destination.Type.Canonical}'");
				map[source.Slot] = destination.Slot;
			}

			return map;
		}

		private void MapFunctions()
		{
			for (var index = 0; index < library.FunctionPool.Count; index++)
			{
				FunctionDef function = library.FunctionPool[index];
				int match = FindTargetFunction(function);
				if (match >= 0)
				{
					_functions[index] = match;
					continue;
				}

				var copy = new FunctionDef
				{
					Name = function.Name,
					OwnerType = function.OwnerType >= 0 ? MapType(function.OwnerType) : -1,
					Flags = function.Flags,
					ParamTypes = [.. function.ParamTypes.Select(ImportSig)],
					ReturnType = ImportSig(function.ReturnType),
					LocalCount = function.LocalCount,
					MaxStack = function.MaxStack,
					SourceFile = function.SourceFile,
					LineTable = [.. function.LineTable],
					Attributes = [.. function.Attributes],
					NativeKey = function.NativeKey,
					NativeTypeArgs = function.NativeTypeArgs >= 0
						? MapSigList(function.NativeTypeArgs)
						: -1,
				};
				target.FunctionPool.Add(copy);
				_functions[index] = target.FunctionPool.Count - 1;
			}
		}

		private int FindTargetFunction(FunctionDef function)
		{
			// Сгенерированные функции библиотеки не могут соответствовать заглушкам:
			// лямбды/инициализаторы всегда добавляются в конец пула потребителя.
			if (function.Name.StartsWith("<lambda", StringComparison.Ordinal) ||
				function.Name.Contains("<static-init>", StringComparison.Ordinal) ||
				(function.Name == "main" && function.OwnerType < 0))
				return -1;

			// Нативные члены сопоставляются по стабильному ключу, а не по сигнатуре.
			if (function.NativeKey is { Length: > 0 } key)
			{
				for (var index = 0; index < target.FunctionPool.Count; index++)
					if (target.FunctionPool[index].NativeKey == key)
						return index;
				return -1;
			}

			int owner = function.OwnerType >= 0 ? MapType(function.OwnerType) : -1;
			for (var index = 0; index < target.FunctionPool.Count; index++)
			{
				FunctionDef candidate = target.FunctionPool[index];
				if (candidate.NativeKey is not null ||
					candidate.Name != function.Name ||
					candidate.ParamCount != function.ParamCount ||
					candidate.OwnerType != owner ||
					(candidate.Flags & ShapeFlags) != (function.Flags & ShapeFlags))
					continue;

				var same = true;
				for (var parameter = 0; parameter < function.ParamTypes.Length; parameter++)
				{
					if (!string.Equals(
						ImportSig(function.ParamTypes[parameter]).Canonical,
						candidate.ParamTypes[parameter].Canonical,
						StringComparison.Ordinal))
					{
						same = false;
						break;
					}
				}

				if (same)
					return index;
			}

			return -1;
		}

		private void MapGlobals()
		{
			for (var index = 0; index < library.FieldPool.Count; index++)
			{
				FieldDef field = library.FieldPool[index];
				TypeSig type = ImportSig(field.Type);
				int match = -1;
				for (var candidate = 0; candidate < target.FieldPool.Count; candidate++)
				{
					FieldDef existing = target.FieldPool[candidate];
					if (existing.Name == field.Name && existing.Type.Canonical == type.Canonical)
					{
						match = candidate;
						break;
					}
				}

				if (match < 0)
				{
					target.FieldPool.Add(new FieldDef
					{
						Name = field.Name,
						Type = type,
						Flags = field.Flags,
						Slot = field.Slot,
						Attributes = [.. field.Attributes],
					});
					match = target.FieldPool.Count - 1;
				}

				_globals[index] = match;
			}
		}

		private void ApplyTypes()
		{
			for (var index = 0; index < library.TypePool.Count; index++)
			{
				TypeDef source = library.TypePool[index];
				TypeDef destination = target.TypePool[_types[index]];
				bool isNew = _newTypes.Contains(_types[index]);

				if (isNew)
				{
					foreach (FieldDef field in source.Fields)
						destination.Fields.Add(new FieldDef
						{
							Name = field.Name,
							Type = ImportSig(field.Type),
							Flags = field.Flags,
							Slot = field.Slot,
							Attributes = [.. field.Attributes],
						});
					destination.Parents = [.. source.Parents.Select(ImportSig)];
					destination.UnionMembers = [.. source.UnionMembers.Select(ImportSig)];
					destination.Attributes = [.. source.Attributes];
				}

				foreach (PropertyDef property in source.Properties)
				{
					if (destination.Properties.Any(existing => existing.Name == property.Name))
						continue;
					destination.Properties.Add(new PropertyDef
					{
						Name = property.Name,
						Type = ImportSig(property.Type),
						Flags = property.Flags,
						OwnerType = _types[index],
						Getter = property.Getter >= 0 ? MapFunction(property.Getter) : -1,
						Setter = property.Setter >= 0 ? MapFunction(property.Setter) : -1,
						Init = property.Init >= 0 ? MapFunction(property.Init) : -1,
						BackingField = property.BackingField >= 0
							? MapSlot(source, property.BackingField)
							: -1,
						Attributes = [.. property.Attributes],
					});
				}

				foreach (int method in source.Methods)
					AddUnique(destination.Methods, MapFunction(method));
				foreach (int constructor in source.Ctors)
					AddUnique(destination.Ctors, MapFunction(constructor));
			}
		}

		private void ApplyFunctions()
		{
			for (var index = 0; index < library.FunctionPool.Count; index++)
			{
				FunctionDef source = library.FunctionPool[index];
				FunctionDef destination = target.FunctionPool[_functions[index]];
				destination.Name = source.Name;
				destination.OwnerType = source.OwnerType >= 0 ? MapType(source.OwnerType) : -1;
				destination.Flags = source.Flags;
				destination.ParamTypes = [.. source.ParamTypes.Select(ImportSig)];
				destination.ReturnType = ImportSig(source.ReturnType);
				destination.LocalCount = source.LocalCount;
				destination.MaxStack = source.MaxStack;
				destination.LineTable = [.. source.LineTable];
				destination.SourceFile = source.SourceFile;
				destination.Attributes = [.. source.Attributes];
				destination.NativeKey = source.NativeKey;
				destination.NativeTypeArgs = source.NativeTypeArgs >= 0
					? MapSigList(source.NativeTypeArgs)
					: -1;
				destination.Code = Relocate(source);
			}
		}

		private int[] Relocate(FunctionDef function)
		{
			int[] code = function.Code;
			var result = new int[code.Length];
			Array.Copy(code, result, code.Length);
			var position = 0;
			while (position < code.Length)
			{
				var op = (OpCode)code[position];
				int operands = OpCodeInfo.OperandCount(op);
				for (var operand = 0; operand < operands; operand++)
				{
					int value = code[position + 1 + operand];
					result[position + 1 + operand] = op switch
					{
						OpCode.PushConst => target.InternConst(library.ConstPool[value]),
						OpCode.NewObject => MapType(value),
						OpCode.NewObjectGeneric when operand == 0 => MapType(value),
						OpCode.NewObjectGeneric => MapSigList(value),
						OpCode.Call or OpCode.CallVirtual or OpCode.CallBase or OpCode.TailCall
							or OpCode.MakeFunc or OpCode.MakeClosure
							when operand == 0 => MapFunction(value),
						OpCode.LoadField or OpCode.StoreField => function.OwnerType >= 0
							? MapSlot(library.TypePool[function.OwnerType], value)
							: value,
						OpCode.LoadStatic or OpCode.StoreStatic => _globals.TryGetValue(value, out int global)
							? global
							: value,
						OpCode.NewList or OpCode.NewArray or OpCode.TypeOfSig or OpCode.IsType
							=> MapSig(value),
						OpCode.NewDict => MapSig(value),
						OpCode.NewTuple => MapSigList(value),
						_ => value,
					};
				}

				position += 1 + operands;
			}

			return result;
		}

		private void MergeStaticInit()
		{
			if (library.StaticInit < 0)
				return;
			int imported = MapFunction(library.StaticInit);
			if (target.StaticInit < 0)
			{
				target.StaticInit = imported;
				return;
			}

			if (target.StaticInit == imported)
				return;

			var thunk = new FunctionDef
			{
				Name = "<static-init>",
				Flags = MemberFlags.Static,
				ParamTypes = [],
				ReturnType = TypeSig.Void,
				LocalCount = 0,
				MaxStack = 4,
				Code =
				[
					(int)OpCode.Call, imported, 0,
					(int)OpCode.Pop,
					(int)OpCode.Call, target.StaticInit, 0,
					(int)OpCode.Pop,
					(int)OpCode.PushVoid,
					(int)OpCode.Ret,
				],
			};
			target.FunctionPool.Add(thunk);
			target.StaticInit = target.FunctionPool.Count - 1;
		}

		private int MapType(int index) =>
			_types.TryGetValue(index, out int mapped) ? mapped : index;

		private int MapFunction(int index) =>
			_functions.TryGetValue(index, out int mapped) ? mapped : index;

		private int MapSig(int index) => target.InternTypeSig(ImportSig(library.TypeSigPool[index]));

		private int MapSigList(int index) =>
			target.InternTypeSigList(library.TypeSigListPool[index].Select(item => ImportSig(library.TypeSigPool[item])));

		private int MapSlot(TypeDef owner, int slot)
		{
			int index = library.TypePool.IndexOf(owner);
			if (index >= 0 && _fieldSlots.TryGetValue(index, out Dictionary<int, int>? map) &&
				map.TryGetValue(slot, out int mapped))
				return mapped;
			return slot;
		}

		private TypeSig ImportSig(TypeSig signature) => signature switch
		{
			TypeSig.NamedSig named => new TypeSig.NamedSig(MapTypeName(named.FullName),
				[.. named.Args.Select(ImportSig)]),
			TypeSig.NullableSig nullable => new TypeSig.NullableSig(ImportSig(nullable.Inner)),
			TypeSig.UnionSig union => new TypeSig.UnionSig([.. union.Members.Select(ImportSig)]),
			TypeSig.FuncSig function => new TypeSig.FuncSig(
				[.. function.Params.Select(ImportSig)],
				ImportSig(function.Return)),
			TypeSig.TupleSig tuple => new TypeSig.TupleSig(
				[.. tuple.Items.Select(ImportSig)],
				tuple.Names),
			_ => signature,
		};

		private string MapTypeName(string fullName)
		{
			if (_typeNames.TryGetValue(fullName, out string? mapped))
				return mapped;
			int dot = fullName.LastIndexOf('.');
			if (dot >= 0 && _simpleTypeNames.TryGetValue(fullName[(dot + 1)..], out string? simple))
				return simple;
			return fullName;
		}

		private static readonly MemberFlags ShapeFlags =
			MemberFlags.Ctor | MemberFlags.Getter | MemberFlags.Setter | MemberFlags.Init |
			MemberFlags.Operator | MemberFlags.Extension | MemberFlags.Static | MemberFlags.Async;

		private static bool IsPadding(string name) =>
			name.StartsWith("$slot", StringComparison.Ordinal);

		private static void AddUnique(List<int> list, int value)
		{
			if (!list.Contains(value))
				list.Add(value);
		}
	}
}
