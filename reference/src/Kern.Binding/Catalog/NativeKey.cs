using System.Collections.Concurrent;
using System.Reflection;
using System.Text;

namespace Kern.Binding.Catalog;

/// <summary>
/// Уникальный стабильный ключ члена: «Тип::Имя`арность(типы параметров)». Различает перегрузки, не зависит от версий BCL.
/// Поле: «Тип::field:Имя» (сеттер — с суффиксом «=»). Ключ можно обратно разрешить в MemberInfo через <see cref="Find"/>.
/// </summary>
public static class NativeKey
{
	private static readonly ConcurrentDictionary<string, MemberInfo?> Cache = new();

	public static string Of(MethodBase m)
	{
		var sb = new StringBuilder(DeclName(m.DeclaringType!)).Append("::");
		sb.Append(m is ConstructorInfo ? (m.IsStatic ? ".cctor" : ".ctor") : m.Name);
		if (m.IsGenericMethodDefinition) sb.Append('`').Append(m.GetGenericArguments().Length);
		sb.Append('(');
		sb.AppendJoin(',', m.GetParameters().Select(p => TypeName(p.ParameterType)));
		return sb.Append(')').ToString();
	}

	public static string Of(FieldInfo f, bool setter = false) =>
		$"{DeclName(f.DeclaringType!)}::field:{f.Name}{(setter ? "=" : "")}";

	public static bool IsFieldSetter(string key) => key.Contains("::field:", StringComparison.Ordinal) && key.EndsWith('=');

	private static string DeclName(Type t) => t.IsGenericType ? t.GetGenericTypeDefinition().FullName! : t.FullName!;

	public static string TypeName(Type t)
	{
		if (t.IsGenericParameter) return (t.DeclaringMethod is not null ? "!!" : "!") + t.GenericParameterPosition;
		if (t.IsByRef) return TypeName(t.GetElementType()!) + "&";
		if (t.IsArray) return TypeName(t.GetElementType()!) + "[" + new string(',', t.GetArrayRank() - 1) + "]";
		if (t.IsGenericType && !t.IsGenericTypeDefinition)
			return t.GetGenericTypeDefinition().FullName + "<" + string.Join(",", t.GetGenericArguments().Select(TypeName)) + ">";
		return t.FullName ?? t.Name;
	}

	/// <summary>Находит член по ключу среди загруженных сборок (MethodInfo/ConstructorInfo/FieldInfo) либо null.</summary>
	public static MemberInfo? Find(string key) => Cache.GetOrAdd(key, FindCore);

	private static MemberInfo? FindCore(string key)
	{
		int sep = key.IndexOf("::", StringComparison.Ordinal);
		if (sep < 0) return null;
		var type = FindType(key[..sep]);
		if (type is null) return null;
		var rest = key[(sep + 2)..];
		const BindingFlags all = BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
		if (rest.StartsWith("field:", StringComparison.Ordinal))
		{
			var name = rest[6..].TrimEnd('=');
			return type.GetField(name, all);
		}
		foreach (var m in type.GetMembers(all | BindingFlags.NonPublic))
			if (m is MethodBase mb && Of(mb) == key) return m;
		return null;
	}

	private static Type? FindType(string fullName)
	{
		foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
		{
			try
			{
				if (asm.IsDynamic) continue;
				if (asm.GetType(fullName, throwOnError: false) is { } t) return t;
			}
			catch (Exception) { /* сборка не читается — пропускаем */ }
		}
		return null;
	}
}
