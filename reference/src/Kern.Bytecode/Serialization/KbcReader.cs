using System.Text;

namespace Kern.Bytecode.Serialization;

/// <summary>Чтение <see cref="KbcModule"/> из формата KBC2 (зеркало <see cref="KbcWriter"/>).</summary>
public sealed class KbcReader
{
	private readonly BinaryReader _r;

	private KbcReader(Stream s) => _r = new BinaryReader(s, new UTF8Encoding(false), leaveOpen: true);

	public static KbcModule Read(byte[] data)
	{
		using var ms = new MemoryStream(data);
		return Read(ms);
	}

	public static KbcModule Read(Stream stream) => new KbcReader(stream).ReadModule();

	private int Count() => _r.Read7BitEncodedInt();
	private string? Str() => _r.ReadBoolean() ? _r.ReadString() : null;

	private List<T> List<T>(Func<T> read)
	{
		int n = Count();
		var list = new List<T>(n);
		for (int i = 0; i < n; i++) list.Add(read());
		return list;
	}

	private KbcModule ReadModule()
	{
		string magic = Encoding.ASCII.GetString(_r.ReadBytes(4));
		if (magic != KbcModule.Magic) throw new InvalidDataException($"не KBC-файл (magic '{magic}')");
		int version = _r.ReadInt32();
		if (version != KbcModule.Version) throw new InvalidDataException($"версия формата {version} не поддерживается (ожидается {KbcModule.Version})");
		var m = new KbcModule { Name = _r.ReadString() };
		m.References = List(() => Str() ?? "");
		m.Strings = List(() => _r.ReadString());
		m.TypeSigs = List(Sig);
		m.TypeSigLists = List(() => { int n = Count(); var a = new int[n]; for (int i = 0; i < n; i++) a[i] = Count(); return a; });
		m.Constants = List(Obj);
		m.Types = List(Type);
		m.Functions = List(Function);
		m.Globals = List(Field);
		m.StaticInit = List(Count);
		m.EntryPoint = _r.ReadInt32();
		m.EntryTakesArgs = _r.ReadBoolean();
		m.EntryIsAsync = _r.ReadBoolean();
		m.Tests = List(Count);
		m.Attributes = List(Attribute);
		return m;
	}

	private TypeSig Sig()
	{
		byte tag = _r.ReadByte();
		switch (tag)
		{
			case 0: return new TypeSig.Prim((PrimKind)_r.ReadByte());
			case 1: { string n = _r.ReadString(); return new TypeSig.Named(n, List(Sig)); }
			case 2: { int scope = _r.ReadByte(); int idx = Count(); return new TypeSig.Param(scope, idx, _r.ReadString()); }
			case 3: { var ps = List(Sig); return new TypeSig.Func(ps, Sig()); }
			case 4:
			{
				var items = List(Sig);
				var names = _r.ReadBoolean() ? List(Str) : null;
				return new TypeSig.Tuple(items, names);
			}
			case 5: return new TypeSig.Nullable(Sig());
			case 6: return new TypeSig.Union(List(Sig));
			default: throw new InvalidDataException($"неизвестный тег подписи типа {tag}");
		}
	}

	private object? Obj()
	{
		byte tag = _r.ReadByte();
		return tag switch
		{
			0 => null,
			1 => _r.ReadBoolean(),
			2 => _r.ReadSByte(),
			3 => _r.ReadInt16(),
			4 => _r.ReadInt32(),
			5 => _r.ReadInt64(),
			6 => _r.ReadByte(),
			7 => _r.ReadUInt16(),
			8 => _r.ReadUInt32(),
			9 => _r.ReadUInt64(),
			10 => _r.ReadHalf(),
			11 => _r.ReadSingle(),
			12 => _r.ReadDouble(),
			13 => (char)_r.ReadUInt16(),
			14 => _r.ReadString(),
			15 => Sig(),
			_ => throw new InvalidDataException($"неизвестный тег константы {tag}"),
		};
	}

	private AttributeUse Attribute()
	{
		var a = new AttributeUse { TypeName = _r.ReadString() };
		a.Args = List(() => { var v = Obj(); return new ConstantArg { Value = v, Name = Str() }; });
		return a;
	}

	private GenericParamDef Generic()
	{
		var g = new GenericParamDef { Name = _r.ReadString() };
		g.Constraints = List(Sig);
		if (_r.ReadBoolean()) g.CtorArgs = List(Sig);
		return g;
	}

	private FieldDef Field()
	{
		var f = new FieldDef { Name = _r.ReadString(), Type = Sig(), Flags = (MemberFlags)_r.ReadUInt32(), Slot = Count() };
		f.Attributes = List(Attribute);
		return f;
	}

	private TypeDef Type()
	{
		var t = new TypeDef { Name = _r.ReadString(), FullName = _r.ReadString(), Kind = (TypeKind)_r.ReadByte(), Flags = (MemberFlags)_r.ReadUInt32() };
		t.GenericParams = List(Generic);
		t.Parents = List(Sig);
		t.ParentInit = List(Count);
		t.Fields = List(Field);
		t.Properties = List(() =>
		{
			var p = new PropertyDef { Name = _r.ReadString(), Type = Sig(), Flags = (MemberFlags)_r.ReadUInt32() };
			p.Getter = _r.ReadInt32(); p.Setter = _r.ReadInt32(); p.Init = _r.ReadInt32(); p.BackingField = _r.ReadInt32();
			p.Attributes = List(Attribute);
			return p;
		});
		t.Methods = List(Count);
		t.Ctors = List(Count);
		t.Attributes = List(Attribute);
		t.UnionMembers = List(Sig);
		t.EnumNames = List(() => _r.ReadString());
		t.EnumValues = List(() => _r.ReadInt64());
		t.ParentChoices = List(() => (_r.ReadString(), _r.ReadInt32()));
		t.NativeType = Str();
		return t;
	}

	private FunctionDef Function()
	{
		var f = new FunctionDef { Name = _r.ReadString(), Owner = _r.ReadInt32(), Flags = (MemberFlags)_r.ReadUInt32() };
		f.GenericParams = List(Generic);
		f.Params = List(() =>
		{
			var p = new ParamDef { Name = _r.ReadString(), Type = Sig(), HasDefault = _r.ReadBoolean(), IsVariadic = _r.ReadBoolean(), DefaultFunction = _r.ReadInt32() };
			if (_r.ReadBoolean()) { var v = Obj(); p.DefaultConstant = new ConstantArg { Value = v, Name = Str() }; }
			return p;
		});
		f.Return = Sig();
		f.OpSymbol = Str();
		if (_r.ReadBoolean()) f.ExtensionOf = Sig();
		f.LocalCount = Count(); f.MaxStack = Count();
		int n = Count();
		var code = new int[n];
		for (int i = 0; i < n; i++) code[i] = _r.Read7BitEncodedInt();
		f.Code = code;
		f.LineTable = List(() => (Count(), Count()));
		f.SourceFile = Str();
		f.Attributes = List(Attribute);
		f.Overrides = _r.ReadInt32();
		f.NativeKey = Str();
		f.CaptureCount = Count();
		return f;
	}
}
