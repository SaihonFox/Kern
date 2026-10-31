using Kern.Bytecode;
using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;

namespace Kern.Compiler.CodeGen;

/// <summary>M3-D: статические поля классов — модульные слоты, чтение и запись.</summary>
public sealed partial class CodeGenerator
{
	/// <summary>Индексы статических полей в <see cref="KbcModule.FieldPool"/> (они же слоты VM).</summary>
	private readonly Dictionary<FieldSymbol, int> _staticSlots = new(ReferenceEqualityComparer.Instance);

	/// <summary>
	/// Статическое поле (в том числе backing-поле статического автосвойства) получает
	/// модульный слот в <see cref="KbcModule.FieldPool"/>: VM индексирует им же массив
	/// статики, поэтому <c>Slot</c> совпадает с индексом в пуле. В <c>TypeDef.Fields</c>
	/// такие поля не попадают — у них нет экземпляра.
	/// </summary>
	private void DefineStaticField(FieldSymbol field)
	{
		var slot = _module.FieldPool.Count;
		_module.FieldPool.Add(new FieldDef
		{
			Name = field.Name,
			Type = TypeSigMap.From(field.Type, _module.Name),
			Slot = slot,
			Flags = MemberFlags.Static | (field.IsRequired ? MemberFlags.Readonly : MemberFlags.None)
		});
		_staticSlots[field] = slot;
	}

	/// <summary>
	/// Чтение статического поля: приёмника нет, операнд — модульный слот
	/// <see cref="KbcModule.FieldPool"/>. Unit-значение читается копией (Q15a2):
	/// иначе <c>local = Type.cache</c> был бы алиасом статического объекта и
	/// последующая запись через цепь (Q13) меняла бы обе переменные.
	/// </summary>
	private void EmitStaticFieldRead(BoundFieldExpression field)
	{
		if (!_staticSlots.TryGetValue(field.Field, out var slot))
		{
			Report("KRN2201", $"Статическое поле '{field.Field.Name}' не попало в FieldPool (M3-D)", field.Span);
			Emit(OpCode.PushVoid);
			return;
		}

		Emit(OpCode.LoadStatic, slot);
		EmitCopyIfUnit(field.Type);
	}

	/// <summary>
	/// Запись статического поля: значение (unit копируется, как при записи в обычное поле)
	/// и <c>StoreStatic</c> в модульный слот. Результат <c>StoreStatic</c> (void) снимается.
	/// </summary>
	private void EmitStaticFieldAssignment(BoundFieldAssignment assignment)
	{
		EmitExpression(assignment.Value);
		EmitCopyIfUnit(assignment.Type);
		if (_staticSlots.TryGetValue(assignment.Field, out var slot))
			Emit(OpCode.StoreStatic, slot);
		else
			Report("KRN2201", $"Статическое поле '{assignment.Field.Name}' не попало в FieldPool (M3-D)", assignment.Span);

		Emit(OpCode.Pop);
	}
}
