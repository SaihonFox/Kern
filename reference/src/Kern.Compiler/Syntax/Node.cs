using Kern.Core;

namespace Kern.Compiler.Syntax;

/// <summary>Базовый узел AST. Неизменяемый, у каждого узла есть диапазон в исходнике.</summary>
public abstract record Node(TextSpan Span)
{
	/// <summary>Прямые дочерние узлы (обход через <see cref="AstWalker"/>).</summary>
	public IEnumerable<Node> Children() => AstWalker.Children(this);
}

/// <summary>Уровень доступа (`pub`/`priv`/`prot`/`int`); Default — не указан.</summary>
public enum Access { Default, Pub, Priv, Prot, Int }

/// <summary>Прочие модификаторы объявления.</summary>
[Flags]
public enum MemberFlags
{
	None = 0,
	Open = 1,       // open
	Override = 2,   // ~
	Abstract = 4,   // @fun / @type
	Static = 8,     // #fun / #type field
	Async = 16,     // async
	Required = 32,  // `!` у поля — обязательное поле
}

/// <summary>Набор модификаторов члена или типа.</summary>
public readonly record struct Modifiers(Access Access, MemberFlags Flags)
{
	public static readonly Modifiers None = new(Access.Default, MemberFlags.None);

	public bool IsOpen => (Flags & MemberFlags.Open) != 0;
	public bool IsOverride => (Flags & MemberFlags.Override) != 0;
	public bool IsAbstract => (Flags & MemberFlags.Abstract) != 0;
	public bool IsStatic => (Flags & MemberFlags.Static) != 0;
	public bool IsAsync => (Flags & MemberFlags.Async) != 0;
	public bool IsRequired => (Flags & MemberFlags.Required) != 0;
}
