using Kern.Binding.Resolution;
using Kern.Lib.Db.Orm;
using Kern.Lib.Db.Sqlite;
using Kern.Tests.Features.Support;
using Kern.Vm.Execution;
using Kern.Vm.Values;
using Xunit;

namespace Kern.Tests.Features;

/// <summary>ORM хранит не-примитивные поля как JSON и разбирает их обратно.</summary>
public sealed class OrmJsonTests
{
	[Fact]
	public void Unit_and_list_fields_round_trip_as_json()
	{
		var program = StdPipeline.Compile("""
			unit address(str city)
			unit person(i64 id, str name, address where, list<i32> scores)
			fun main {}
			""");
		var interpreter = new Interpreter(program.Module, new NativeResolver(StdPipeline.Catalog()));
		Kern.Vm.Types.KClassType addressType = interpreter.Types.Named(
			program.Module.TypePool.First(type => type.Name == "address"));
		Kern.Vm.Types.KClassType personType = interpreter.Types.Named(
			program.Module.TypePool.First(type => type.Name == "person"));

		using var db = new KernSqlite(interpreter, ":memory:");
		var table = new OrmTable(db, interpreter, personType);
		Assert.True(table.Create().IsOk);

		var address = new KObject(addressType, [KValue.Str("Москва")]);
		var scores = new KList(
			interpreter.Types.Prim(Kern.Bytecode.PrimKind.I32),
			[KValue.I32(1), KValue.I32(2)]);
		var person = new KObject(personType,
		[
			KValue.I64(0),
			KValue.Str("Аня"),
			KValue.Obj(address),
			KValue.Obj(scores),
		]);

		Assert.True(table.Insert(KValue.Obj(person)).IsOk);

		KValue all = table.All();
		var result = Assert.IsType<KObject>(all.Ref);
		Assert.Equal("Ok", result.Type.Definition.Name);
		var rows = Assert.IsType<KList>(result.GetField(0).Ref);
		var loaded = Assert.IsType<KObject>(Assert.Single(rows.Items).Ref);
		var loadedAddress = Assert.IsType<KObject>(loaded.GetField(2).Ref);
		Assert.Equal("Москва", loadedAddress.GetField(0).AsStr);
		var loadedScores = Assert.IsType<KList>(loaded.GetField(3).Ref);
		Assert.Equal(new[] { 1, 2 }, loadedScores.Items.Select(item => item.AsI32));
	}
}
