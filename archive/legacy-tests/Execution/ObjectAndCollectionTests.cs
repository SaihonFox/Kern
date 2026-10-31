using Kern.Bytecode;
using Kern.Vm.Execution;
using Kern.Vm.Types;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Execution;

/// <summary>M5: объекты, поля, статика, коллекции, кортежи и функции-значения.</summary>
public sealed class ObjectAndCollectionTests
{
	[Fact]
	public void NewObject_has_none_fields_and_field_store_load_works()
	{
		var module = ModuleWithType(TypeDefKind.Unit);
		module.FunctionPool.Add(new FunctionDef
		{
			Name = "main",
			OwnerType = 0,
			LocalCount = 1,
			MaxStack = 8,
			Code =
			[
				(int)OpCode.NewObject, 0,
				(int)OpCode.Dup,
				(int)OpCode.PushI32, 7,
				(int)OpCode.StoreField, 0,
				(int)OpCode.Pop,
				(int)OpCode.LoadField, 0,
				(int)OpCode.Ret,
			],
		});

		Assert.Equal(KValue.I32(7), new Interpreter(module).Run("main"));
	}

	[Fact]
	public void NewObject_defaults_fields_to_none()
	{
		var module = ModuleWithType(TypeDefKind.Unit);
		module.FunctionPool.Add(new FunctionDef
		{
			Name = "main",
			OwnerType = 0,
			LocalCount = 0,
			MaxStack = 4,
			Code =
			[
				(int)OpCode.NewObject, 0,
				(int)OpCode.LoadField, 1,
				(int)OpCode.Ret,
			],
		});

		Assert.True(new Interpreter(module).Run("main").IsNone);
	}

	[Fact]
	public void CopyUnit_makes_an_independent_value_copy()
	{
		var module = ModuleWithType(TypeDefKind.Unit);
		module.FunctionPool.Add(new FunctionDef
		{
			Name = "main",
			OwnerType = 0,
			LocalCount = 2,
			MaxStack = 8,
			Code =
			[
				(int)OpCode.NewObject, 0,
				(int)OpCode.StoreLocal, 0,
				(int)OpCode.LoadLocal, 0,
				(int)OpCode.CopyUnit,
				(int)OpCode.StoreLocal, 1,
				(int)OpCode.LoadLocal, 1,
				(int)OpCode.PushI32, 9,
				(int)OpCode.StoreField, 0,
				(int)OpCode.Pop,
				(int)OpCode.LoadLocal, 0,
				(int)OpCode.LoadField, 0,
				(int)OpCode.Ret,
			],
		});

		Assert.True(new Interpreter(module).Run("main").IsNone);
	}

	[Fact]
	public void Static_field_round_trips()
	{
		var module = new KbcModule { Name = "test" };
		module.FieldPool.Add(new FieldDef { Name = "counter", Type = TypeSig.I32, Slot = 0 });
		module.FunctionPool.Add(new FunctionDef
		{
			Name = "main",
			MaxStack = 4,
			Code =
			[
				(int)OpCode.PushI32, 5,
				(int)OpCode.StoreStatic, 0,
				(int)OpCode.Pop,
				(int)OpCode.LoadStatic, 0,
				(int)OpCode.Ret,
			],
		});

		Assert.Equal(KValue.I32(5), new Interpreter(module).Run("main"));
	}

	[Fact]
	public void NewList_keeps_element_order()
	{
		var module = ListModule(OpCode.NewList);
		var list = Assert.IsType<KList>(new Interpreter(module).Run("main").Ref);

		Assert.Equal(2, list.Count);
		Assert.Equal(KValue.I32(1), list[0]);
		Assert.Equal(KValue.I32(2), list[1]);
		Assert.Equal(PrimKind.I32, Assert.IsType<KPrimType>(list.ElementType).Prim);
	}

	[Fact]
	public void NewArray_keeps_element_order()
	{
		var module = ListModule(OpCode.NewArray);
		var array = Assert.IsType<KArray>(new Interpreter(module).Run("main").Ref);

		Assert.Equal(2, array.Length);
		Assert.Equal(KValue.I32(1), array[0]);
		Assert.Equal(KValue.I32(2), array[1]);
	}

	[Fact]
	public void NewDict_creates_empty_typed_dict()
	{
		var module = new KbcModule { Name = "test" };
		module.TypeSigPool.Add(TypeSig.Str);
		module.TypeSigPool.Add(TypeSig.I32);
		module.FunctionPool.Add(new FunctionDef
		{
			Name = "main",
			MaxStack = 4,
			Code =
			[
				(int)OpCode.NewDict, 0, 1,
				(int)OpCode.Ret,
			],
		});

		var dict = Assert.IsType<KDict>(new Interpreter(module).Run("main").Ref);
		Assert.Equal(0, dict.Count);
		Assert.Equal("str", dict.KeyType!.Canonical);
		Assert.Equal("i32", dict.ValueType!.Canonical);
	}

	[Fact]
	public void NewTuple_keeps_values_and_types()
	{
		var module = new KbcModule { Name = "test" };
		module.TypeSigPool.Add(TypeSig.I32);
		module.TypeSigPool.Add(TypeSig.Str);
		module.TypeSigListPool.Add([0, 1]);
		module.FunctionPool.Add(new FunctionDef
		{
			Name = "main",
			MaxStack = 4,
			Code =
			[
				(int)OpCode.PushI32, 1,
				(int)OpCode.PushConst, 0,
				(int)OpCode.NewTuple, 0,
				(int)OpCode.Ret,
			],
		});
		module.ConstPool.Add(KConst.Str("x"));

		var tuple = Assert.IsType<KTuple>(new Interpreter(module).Run("main").Ref);
		Assert.Equal(2, tuple.Count);
		Assert.Equal(KValue.I32(1), tuple[0]);
		Assert.Equal(KValue.Str("x"), tuple[1]);
	}

	[Fact]
	public void TypeOfSig_and_TypeOfValue_produce_interned_types()
	{
		var module = new KbcModule { Name = "test" };
		module.TypeSigPool.Add(TypeSig.I32);
		module.FunctionPool.Add(new FunctionDef
		{
			Name = "main",
			MaxStack = 4,
			Code =
			[
				(int)OpCode.PushI32, 5,
				(int)OpCode.TypeOfValue,
				(int)OpCode.Ret,
			],
		});

		var type = Assert.IsType<KPrimType>(new Interpreter(module).Run("main").Ref);
		Assert.Equal(PrimKind.I32, type.Prim);
	}

	[Fact]
	public void IsType_checks_primitive_types()
	{
		var module = new KbcModule { Name = "test" };
		module.TypeSigPool.Add(TypeSig.I32);
		module.TypeSigPool.Add(TypeSig.Str);
		module.FunctionPool.Add(new FunctionDef
		{
			Name = "main",
			MaxStack = 4,
			Code =
			[
				(int)OpCode.PushI32, 5,
				(int)OpCode.IsType, 1,
				(int)OpCode.Ret,
			],
		});

		Assert.Equal(KValue.False, new Interpreter(module).Run("main"));
	}

	[Fact]
	public void MakeFunc_and_CallValue_invoke_a_function_value()
	{
		var module = new KbcModule { Name = "test" };
		module.FunctionPool.Add(new FunctionDef
		{
			Name = "add",
			ParamTypes = [TypeSig.I32, TypeSig.I32],
			ReturnType = TypeSig.I32,
			LocalCount = 2,
			MaxStack = 4,
			Code =
			[
				(int)OpCode.LoadLocal, 0,
				(int)OpCode.LoadLocal, 1,
				(int)OpCode.Add,
				(int)OpCode.Ret,
			],
		});
		module.FunctionPool.Add(new FunctionDef
		{
			Name = "main",
			MaxStack = 8,
			Code =
			[
				(int)OpCode.MakeFunc, 0,
				(int)OpCode.PushI32, 2,
				(int)OpCode.PushI32, 3,
				(int)OpCode.CallValue, 2,
				(int)OpCode.Ret,
			],
		});

		Assert.Equal(KValue.I32(5), new Interpreter(module).Run("main"));
	}

	[Fact]
	public void MakeClosure_and_LoadCapture_read_captured_value()
	{
		var module = new KbcModule { Name = "test" };
		module.FunctionPool.Add(new FunctionDef
		{
			Name = "get",
			LocalCount = 1,
			MaxStack = 2,
			Code =
			[
				(int)OpCode.LoadCapture, 0,
				(int)OpCode.Ret,
			],
		});
		module.FunctionPool.Add(new FunctionDef
		{
			Name = "main",
			MaxStack = 4,
			Code =
			[
				(int)OpCode.PushI32, 42,
				(int)OpCode.MakeClosure, 0, 1,
				(int)OpCode.CallValue, 0,
				(int)OpCode.Ret,
			],
		});

		Assert.Equal(KValue.I32(42), new Interpreter(module).Run("main"));
	}

	private static KbcModule ModuleWithType(TypeDefKind kind)
	{
		var module = new KbcModule { Name = "test" };
		var definition = new TypeDef { Name = "Point", FullName = "test.Point", Kind = kind };
		definition.Fields.Add(new FieldDef { Name = "x", Type = TypeSig.I32, Slot = 0 });
		definition.Fields.Add(new FieldDef { Name = "y", Type = TypeSig.I32, Slot = 1 });
		module.TypePool.Add(definition);
		return module;
	}

	private static KbcModule ListModule(OpCode op)
	{
		var module = new KbcModule { Name = "test" };
		module.TypeSigPool.Add(TypeSig.I32);
		module.FunctionPool.Add(new FunctionDef
		{
			Name = "main",
			MaxStack = 4,
			Code =
			[
				(int)OpCode.PushI32, 1,
				(int)OpCode.PushI32, 2,
				(int)op, 0, 2,
				(int)OpCode.Ret,
			],
		});
		return module;
	}
}
