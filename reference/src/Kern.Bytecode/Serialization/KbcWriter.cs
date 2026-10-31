using System.Text;

namespace Kern.Bytecode.Serialization;

/// <summary>Бинарная запись <see cref="KbcModule"/> (формат KBC2). Порядок секций фиксирован и зеркален <see cref="KbcReader"/>.</summary>
public sealed class KbcWriter
{
	private readonly BinaryWriter _w;

	private KbcWriter(Stream s) => _w = new BinaryWriter(s, new UTF8Encoding(false), leaveOpen: true);

	public static byte[] Write(KbcModule m)
	{
		using var ms = new MemoryStream();
		Write(m, ms);
		return ms.ToArray();
	}

	public static void Write(KbcModule m, Stream stream)
	{
		var w = new KbcWriter(stream);
		w.WriteModule(m);
		w._w.Flush();
	}

	private void Count(int n) => _w.Write7BitEncodedInt(n);
	private void Str(string? s) { _w.Write(s is not null); if (s is not null) _w.Write(s); }

	private void WriteModule(KbcModule m)
	{
		_w.Write(Encoding.ASCII.GetBytes(KbcModule.Magic));
		_w.Write(KbcModule.Version);
		_w.Write(m.Name);
		List(m.References, Str);
		List(m.Strings, s => _w.Write(s));
		List(m.TypeSigs, Sig);
		List(m.TypeSigLists, l => { Count(l.Length); foreach (int i in l) Count(i); });
		List(m.Constants, Obj);
		List(m.Types, Type);
		List(m.Functions, Function);
		List(m.Globals, Field);
		List(m.StaticInit, Count);
		_w.Write(m.EntryPoint);
		_w.Write(m.EntryTakesArgs);
		_w.Write(m.EntryIsAsync);
		List(m.Tests, Count);
		List(m.Attributes, Attribute);
	}

	private void List<T>(IReadOnlyList<T> items, Action<T> write)
	{
		Count(items.Count);
		foreach (var i in items) write(i);
	}

	private void Sig(TypeSig s)
	{
		switch (s)
		{
			case TypeSig.Prim p: _w.Write((byte)0); _w.Write((byte)p.Kind); break;
			case TypeSig.Named n: _w.Write((byte)1); _w.Write(n.FullName); List(n.Args, Sig); break;
			case TypeSig.Param p: _w.Write((byte)2); _w.Write((byte)p.Scope); Count(p.Index); _w.Write(p.Name); break;
			case TypeSig.Func f: _w.Write((byte)3); List(f.Params, Sig); Sig(f.Return); break;
			case TypeSig.Tuple t:
				_w.Write((byte)4); List(t.Items, Sig);
				_w.Write(t.Names is not null);
				if (t.Names is not null) List(t.Names, Str);
				break;
			case TypeSig.Nullable n: _w.Write((byte)5); Sig(n.Inner); break;
			case TypeSig.Union u: _w.Write((byte)6); List(u.Members, Sig); break;
			default: throw new InvalidOperationException($"неизвестная подпись типа {s}");
		}
	}

	private void Obj(object? o)
	{
		switch (o)
		{
			case null: _w.Write((byte)0); break;
			case bool b: _w.Write((byte)1); _w.Write(b); break;
			case sbyte x: _w.Write((byte)2); _w.Write(x); break;
			case short x: _w.Write((byte)3); _w.Write(x); break;
			case int x: _w.Write((byte)4); _w.Write(x); break;
			case long x: _w.Write((byte)5); _w.Write(x); break;
			case byte x: _w.Write((byte)6); _w.Write(x); break;
			case ushort x: _w.Write((byte)7); _w.Write(x); break;
			case uint x: _w.Write((byte)8); _w.Write(x); break;
			case ulong x: _w.Write((byte)9); _w.Write(x); break;
			case Half x: _w.Write((byte)10); _w.Write(x); break;
			case float x: _w.Write((byte)11); _w.Write(x); break;
			case double x: _w.Write((byte)12); _w.Write(x); break;
			case char x: _w.Write((byte)13); _w.Write((ushort)x); break;
			case string x: _w.Write((byte)14); _w.Write(x); break;
			case TypeSig x: _w.Write((byte)15); Sig(x); break;
			default: throw new InvalidOperationException($"константа типа {o.GetType().Name} не сериализуется");
		}
	}

	private void Attribute(AttributeUse a)
	{
		_w.Write(a.TypeName);
		List(a.Args, c => { Obj(c.Value); Str(c.Name); });
	}

	private void Generic(GenericParamDef g)
	{
		_w.Write(g.Name);
		List(g.Constraints, Sig);
		_w.Write(g.CtorArgs is not null);
		if (g.CtorArgs is not null) List(g.CtorArgs, Sig);
	}

	private void Field(FieldDef f)
	{
		_w.Write(f.Name); Sig(f.Type); _w.Write((uint)f.Flags); Count(f.Slot);
		List(f.Attributes, Attribute);
	}

	private void Type(TypeDef t)
	{
		_w.Write(t.Name); _w.Write(t.FullName); _w.Write((byte)t.Kind); _w.Write((uint)t.Flags);
		List(t.GenericParams, Generic);
		List(t.Parents, Sig);
		List(t.ParentInit, Count);
		List(t.Fields, Field);
		List(t.Properties, p =>
		{
			_w.Write(p.Name); Sig(p.Type); _w.Write((uint)p.Flags);
			_w.Write(p.Getter); _w.Write(p.Setter); _w.Write(p.Init); _w.Write(p.BackingField);
			List(p.Attributes, Attribute);
		});
		List(t.Methods, Count);
		List(t.Ctors, Count);
		List(t.Attributes, Attribute);
		List(t.UnionMembers, Sig);
		List(t.EnumNames, s => _w.Write(s));
		List(t.EnumValues, v => _w.Write(v));
		List(t.ParentChoices, c => { _w.Write(c.Member); _w.Write(c.Parent); });
		Str(t.NativeType);
	}

	private void Function(FunctionDef f)
	{
		_w.Write(f.Name); _w.Write(f.Owner); _w.Write((uint)f.Flags);
		List(f.GenericParams, Generic);
		List(f.Params, p =>
		{
			_w.Write(p.Name); Sig(p.Type); _w.Write(p.HasDefault); _w.Write(p.IsVariadic); _w.Write(p.DefaultFunction);
			_w.Write(p.DefaultConstant is not null);
			if (p.DefaultConstant is { } dc) { Obj(dc.Value); Str(dc.Name); }
		});
		Sig(f.Return);
		Str(f.OpSymbol);
		_w.Write(f.ExtensionOf is not null);
		if (f.ExtensionOf is not null) Sig(f.ExtensionOf);
		Count(f.LocalCount); Count(f.MaxStack);
		Count(f.Code.Length);
		foreach (int c in f.Code) _w.Write7BitEncodedInt(c);
		List(f.LineTable, l => { Count(l.Offset); Count(l.Line); });
		Str(f.SourceFile);
		List(f.Attributes, Attribute);
		_w.Write(f.Overrides);
		Str(f.NativeKey);
		Count(f.CaptureCount);
	}
}
