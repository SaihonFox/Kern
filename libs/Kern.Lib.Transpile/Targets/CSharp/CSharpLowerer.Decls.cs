using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Lib.Transpile.Core;

namespace Kern.Lib.Transpile.Targets.CSharp;

public sealed partial class CSharpLowerer
{
	protected override void EmitModule()
	{
		W.Line("#nullable enable");
		W.Line("using System;");
		W.Line("using System.Collections.Generic;");
		W.Line("using System.Linq;");
		W.Line("using System.Threading.Tasks;");
		W.Line();
		W.Line($"namespace {Namespace};");
		W.Line();

		IReadOnlyList<FieldSymbol> globals = [.. Module.Symbol.Globals.Values];
		List<BoundFunction> topLevel = [.. TopLevelFunctions];
		List<BoundFunction> extensions = [.. ExtensionFunctions];
		BoundFunction? staticInit = Module.Functions.FirstOrDefault(
			function => function.Symbol.Name == "<static-init>" && function.Symbol.Owner is null);
		if (globals.Count > 0 || topLevel.Count > 0 || extensions.Count > 0 || staticInit is not null)
		{
			W.Open($"public static partial class {ModuleClass(Module)}");
			foreach (FieldSymbol field in globals)
				EmitGlobal(field);
			foreach (BoundFunction function in topLevel)
			{
				EmitFunction(function, null, extension: false);
				W.Line();
			}

			foreach (BoundFunction function in extensions)
			{
				EmitFunction(function, null, extension: true);
				W.Line();
			}

			if (staticInit is not null)
			{
				BeginFunction();
				W.Open($"static {ModuleClass(Module)}()");
				EmitBody(staticInit.Body, PrimitiveType.Void);
				W.Close();
			}

			W.Close();
			W.Line();
		}

		foreach (TypeSymbol type in TypeDeclarations)
		{
			EmitTypeDeclaration(type);
			W.Line();
		}
	}

	protected override void EmitGlobal(FieldSymbol field)
	{
		W.Line($"public static {TypeName(field.Type)} {MemberName(field.Name)} = {ZeroOrNull(field.Type)};");
	}

	// ------------------------------------------------------------------ функции

	private void EmitFunction(BoundFunction method, TypeSymbol? owner, bool extension)
	{
		BeginFunction();
		ThisAlias = extension ? "self" : owner is not null && !method.Symbol.IsStatic ? "this" : null;
		foreach (BoundParameter parameter in method.Parameters)
		{
			if (parameter.Symbol.Name == "$")
			{
				RegisterSlot(parameter.Slot, ThisAlias ?? "this");
				continue;
			}

			NameOfSlot(parameter.Slot, parameter.Symbol.Name);
		}

		FunctionSymbol symbol = method.Symbol;
		string access = Access(symbol.Accessibility);
		string modifiers = access;
		if (owner is null || symbol.IsStatic || extension)
			modifiers += "static ";
		else if (symbol.IsAbstract)
			modifiers += "abstract ";
		else if (symbol.IsOverride)
			modifiers += "override ";
		else if (symbol.IsOpen)
			modifiers += "virtual ";
		if (symbol.IsAsync)
			modifiers += "async ";

		string returnType = symbol.IsAsync
			? (IsVoid(method.ReturnType) ? "Task" : $"Task<{TypeName(method.ReturnType)}>")
			: TypeName(method.ReturnType);
		string name = symbol.IsOperator ? OperatorName(symbol) : MemberName(symbol.Name);
		string generic = GenericParameters(symbol.GenericParameters);
		string parameters = ParameterList(method, extension);
		string head = $"{modifiers}{returnType} {name}{generic}({parameters})";
		if (symbol.IsAbstract || method.Body.Statements.Count == 0 && method.Body.Value is null &&
			symbol.IsAbstract)
		{
			W.Line(head + ";");
			return;
		}

		W.Open(head);
		EmitBody(method.Body, method.ReturnType);
		W.Close();
	}

	protected override void EmitMethod(TypeSymbol owner, BoundFunction method) =>
		EmitFunction(method, owner, extension: method.Symbol.ReceiverType is not null);

	private string ParameterList(BoundFunction method, bool extension)
	{
		var parts = new List<string>();
		if (extension && method.Symbol.ReceiverType is { } receiver)
			parts.Add($"this {TypeName(receiver)} self");
		foreach (BoundParameter parameter in method.Parameters)
		{
			if (parameter.Symbol.Name == "$")
				continue;
			string name = NameOfSlot(parameter.Slot, parameter.Symbol.Name);
			parts.Add($"{TypeName(parameter.Symbol.Type)} {name}");
		}

		return string.Join(", ", parts);
	}

	private static string GenericParameters(IReadOnlyList<TypeParameterType> parameters) =>
		parameters.Count == 0
			? ""
			: "<" + string.Join(", ", parameters.Select(parameter => Naming.Sanitize(parameter.Name))) + ">";

	private static string Access(Accessibility accessibility) => accessibility switch
	{
		Accessibility.Private => "private ",
		Accessibility.Protected => "protected ",
		Accessibility.Internal => "internal ",
		_ => "public "
	};

	// ------------------------------------------------------------------ типы

	protected override void EmitTypeDeclaration(TypeSymbol type)
	{
		CurrentType = type;
		try
		{
			if (type.Kind == SymbolKind.UnionAlias)
			{
				W.Line($"// union {type.Name} = {string.Join(" | ", type.UnionAlternatives.Select(TypeName))} (в C# — object)");
				return;
			}

			if (type.IsValueType)
				EmitUnit(type);
			else
				EmitClass(type);
		}
		finally
		{
			CurrentType = null;
		}
	}

	private void EmitUnit(TypeSymbol type)
	{
		var positional = new List<BoundParameter>();
		BoundFunction? primary = type.PrimaryConstructor is { } primarySymbol ? BoundOf(primarySymbol) : null;
		if (primary is not null)
			positional.AddRange(primary.Parameters.Where(parameter => parameter.Symbol.Name != "$"));

		string keyword = type.IsImmutable ? "readonly record struct" : "record struct";
		string parameters = string.Join(", ", positional.Select(parameter =>
			$"{TypeName(parameter.Symbol.Type)} {MemberName(parameter.Symbol.Name)}"));
		W.Open($"{Access(type.Accessibility)}{keyword} {Naming.Pascal(type.Name)}{GenericParameters(type.GenericParameters)}({parameters})");
		EmitMembers(type, positional.Select(parameter => parameter.Symbol.Name).ToHashSet(StringComparer.Ordinal));
		W.Close();
	}

	private void EmitClass(TypeSymbol type)
	{
		string baseType = type.Bases.Count > 0 ? " : " + TypeName(type.Bases[0]) : "";
		string modifiers = type.IsAbstract ? "abstract " : type.IsOpen ? "" : "sealed ";
		W.Open($"{Access(type.Accessibility)}{modifiers}partial class {Naming.Pascal(type.Name)}" +
			$"{GenericParameters(type.GenericParameters)}{baseType}");
		EmitMembers(type, []);
		W.Close();
	}

	private void EmitMembers(TypeSymbol type, HashSet<string> positional)
	{
		foreach (FieldSymbol field in type.Members.Fields)
		{
			if (!ReferenceEquals(field.Owner, type) || IsAutoBacking(field) ||
				positional.Contains(field.Name))
				continue;
			EmitField(field);
		}

		foreach (PropertySymbol property in type.Members.Properties)
		{
			if (!ReferenceEquals(property.Owner, type) || property.IsExtension ||
				positional.Contains(property.Name))
				continue;
			EmitProperty(
				property,
				type,
				property.Getter is { } getter ? BoundOf(getter) : null,
				property.Setter is { } setter ? BoundOf(setter) : null,
				property.Init is { } init ? BoundOf(init) : null);
		}

		foreach (BoundFunction constructor in MembersOf(type).Where(function => function.Symbol.IsConstructor))
		{
			if (constructor.Symbol.IsPrimaryConstructor && type.IsValueType)
				continue;
			EmitConstructor(type, constructor);
		}

		foreach (BoundFunction method in MembersOf(type))
		{
			FunctionSymbol symbol = method.Symbol;
			if (symbol.IsConstructor || symbol.IsAccessor || symbol.Name == "<static-init>")
				continue;
			EmitMethod(type, method);
			W.Line();
		}

		if (type.Members.StaticInitializer is { } staticInitializer && BoundOf(staticInitializer) is { } initFunction)
		{
			BeginFunction();
			ThisAlias = null;
			W.Open($"static {Naming.Pascal(type.Name)}()");
			EmitBody(initFunction.Body, PrimitiveType.Void);
			W.Close();
		}
	}

	protected override void EmitField(FieldSymbol field)
	{
		string modifiers = field.IsBackingField
			? "private "
			: Access(field.Accessibility) + (field.IsStatic ? "static " : "");
		W.Line($"{modifiers}{TypeName(field.Type)} {FieldMemberName(field)} = {ZeroOrNull(field.Type)};");
	}

	protected override void EmitProperty(
		PropertySymbol property,
		TypeSymbol owner,
		BoundFunction? getter,
		BoundFunction? setter,
		BoundFunction? init)
	{
		string head = $"{Access(property.Accessibility)}{(property.IsStatic ? "static " : "")}" +
			$"{TypeName(property.Type)} {MemberName(property.Name)}";
		bool custom = !property.IsAuto && (getter?.Body.Value is not null || setter?.Body.Value is not null);
		if (!custom)
		{
			string setterText = property.CanWrite || property.CanInit ? "set; " : "private set; ";
			W.Line($"{head} {{ get; {setterText}}}");
			return;
		}

		W.Open(head);
		if (getter is not null)
		{
			BeginFunction();
			ThisAlias = property.IsStatic ? null : "this";
			RegisterSelf(getter);
			W.Open("get");
			EmitBody(getter.Body, getter.ReturnType);
			W.Close();
		}

		BoundFunction? writer = setter ?? init;
		if (writer is not null)
		{
			BeginFunction();
			ThisAlias = property.IsStatic ? null : "this";
			RegisterSelf(writer);
			W.Open(property.IsStatic ? "set" : "set");
			EmitBody(writer.Body, PrimitiveType.Void);
			W.Close();
		}

		W.Close();
	}

	private void RegisterSelf(BoundFunction function)
	{
		foreach (BoundParameter parameter in function.Parameters)
		{
			if (parameter.Symbol.Name == "$")
				RegisterSlot(parameter.Slot, ThisAlias ?? "this");
			else
				NameOfSlot(parameter.Slot, parameter.Symbol.Name);
		}
	}

	protected override void EmitConstructor(TypeSymbol type, BoundFunction constructor)
	{
		BeginFunction();
		ThisAlias = "this";
		foreach (BoundParameter parameter in constructor.Parameters)
		{
			if (parameter.Symbol.Name == "$")
			{
				RegisterSlot(parameter.Slot, "this");
				continue;
			}

			NameOfSlot(parameter.Slot, parameter.Symbol.Name);
		}

		string parameters = string.Join(", ", constructor.Parameters
			.Where(parameter => parameter.Symbol.Name != "$")
			.Select(parameter => $"{TypeName(parameter.Symbol.Type)} {NameOfSlot(parameter.Slot, parameter.Symbol.Name)}"));

		string chain = "";
		if (constructor.Chain is { } constructorChain)
		{
			bool toThis = ReferenceEquals(constructorChain.Target.Owner, type);
			chain = $" : {(toThis ? "this" : "base")}({string.Join(", ", constructorChain.Arguments.Select(Value))})";
		}
		else if (constructor.BaseCalls.Count > 0 && constructor.BaseCalls[0].Arguments.Count > 0)
		{
			chain = $" : base({string.Join(", ", constructor.BaseCalls[0].Arguments.Select(Value))})";
		}

		W.Open($"{Access(constructor.Symbol.Accessibility)}{Naming.Pascal(type.Name)}({parameters}){chain}");
		if (constructor.Chain is null)
		{
			foreach (BoundFieldInitializer initializer in constructor.Initializers)
			{
				if (ReferenceEquals(initializer.Field.Owner, type) && !initializer.Field.IsStatic)
					W.Line($"this.{MemberName(initializer.Field.Name)} = {StripParens(Value(initializer.Value))};");
			}

			foreach (BoundParameter parameter in constructor.Parameters)
			{
				if (parameter.Symbol.Field is { } field && !field.IsStatic && parameter.Symbol.Name != "$")
					W.Line($"this.{MemberName(field.Name)} = {NameOfSlot(parameter.Slot, parameter.Symbol.Name)};");
			}
		}

		EmitBody(constructor.Body, PrimitiveType.Void, isCtor: true);
		W.Close();
	}
}
