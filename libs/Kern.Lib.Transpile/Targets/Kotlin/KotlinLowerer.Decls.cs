using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Compiler.Syntax;
using Kern.Lib.Transpile.Core;

namespace Kern.Lib.Transpile.Targets.Kotlin;

public sealed partial class KotlinLowerer
{
	protected override void EmitModule()
	{
		W.Line($"package {PackageOf(Module)}");
		W.Line();
		W.Line("import kern.runtime.*");
		W.Line();

		IReadOnlyList<FieldSymbol> globals = [.. Module.Symbol.Globals.Values];
		List<BoundFunction> topLevel = [.. TopLevelFunctions];
		List<BoundFunction> extensions = [.. ExtensionFunctions];
		BoundFunction? staticInit = Module.Functions.FirstOrDefault(
			function => function.Symbol.Name == "<static-init>" && function.Symbol.Owner is null);

		foreach (FieldSymbol field in globals)
			EmitGlobal(field);
		if (globals.Count > 0)
			W.Line();

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

		foreach (TypeSymbol type in TypeDeclarations)
		{
			EmitTypeDeclaration(type);
			W.Line();
		}

		if (staticInit is not null)
		{
			BeginFunction();
			W.Open("private val __kernInit = run");
			EmitBody(staticInit.Body, PrimitiveType.Void);
			W.Close();
			W.Line();
		}
	}

	protected override void EmitGlobal(FieldSymbol field)
	{
		W.Line($"var {MemberName(field.Name)}: {TypeName(field.Type)} = {ZeroOrNull(field.Type)}");
		if (field.Initializer is not null)
			Warnings.Add($"инициализатор глобальной '{field.Name}' не перенесён — нужен moduleInit");
	}

	// ------------------------------------------------------------------ функции

	private void EmitFunction(BoundFunction method, TypeSymbol? owner, bool extension)
	{
		BeginFunction();
		ThisAlias = extension ? "this" : owner is not null && !method.Symbol.IsStatic ? "this" : null;
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
		string modifiers = Access(symbol.Accessibility);
		if (owner is not null && !symbol.IsStatic)
		{
			if (symbol.IsAbstract)
				modifiers += "abstract ";
			else if (symbol.IsOverride)
				modifiers += "override ";
			else if (symbol.IsOpen)
				modifiers += "open ";
		}

		if (symbol.IsAsync)
			modifiers += "suspend ";
		string name = symbol.IsOperator ? OperatorName(symbol) : MemberName(symbol.Name);
		string generic = GenericParameters(symbol.GenericParameters);
		string receiver = extension && symbol.ReceiverType is { } receiverType
			? TypeName(receiverType) + "."
			: "";
		string parameters = string.Join(", ", method.Parameters
			.Where(parameter => parameter.Symbol.Name != "$")
			.Select(parameter =>
				$"{NameOfSlot(parameter.Slot, parameter.Symbol.Name)}: {TypeName(parameter.Symbol.Type)}"));
		string returnType = IsVoid(method.ReturnType) ? "" : ": " + TypeName(method.ReturnType);
		string head = $"{modifiers}fun {(generic.Length == 0 ? "" : generic + " ")}{receiver}{name}({parameters}){returnType}";
		if (symbol.IsAbstract)
		{
			W.Line(head);
			return;
		}

		W.Open(head);
		EmitBody(method.Body, method.ReturnType);
		W.Close();
	}

	protected override void EmitMethod(TypeSymbol owner, BoundFunction method) =>
		EmitFunction(method, owner, extension: method.Symbol.ReceiverType is not null);

	private static string GenericParameters(IReadOnlyList<TypeParameterType> parameters) =>
		parameters.Count == 0
			? ""
			: "<" + string.Join(", ", parameters.Select(parameter => Naming.Sanitize(parameter.Name))) + ">";

	private static string Access(Accessibility accessibility) => accessibility switch
	{
		Accessibility.Private => "private ",
		Accessibility.Protected => "protected ",
		Accessibility.Internal => "internal ",
		_ => ""
	};

	// ------------------------------------------------------------------ типы

	protected override void EmitTypeDeclaration(TypeSymbol type)
	{
		CurrentType = type;
		try
		{
			if (type.Kind == SymbolKind.UnionAlias)
			{
				W.Line($"// union {type.Name} = {string.Join(" | ", type.UnionAlternatives.Select(TypeName))} (в Kotlin — Any)");
				return;
			}

			EmitClass(type);
		}
		finally
		{
			CurrentType = null;
		}
	}

	private void EmitClass(TypeSymbol type)
	{
		BoundFunction? primary = type.PrimaryConstructor is { } primarySymbol ? BoundOf(primarySymbol) : null;
		var fromParameters = new HashSet<string>(StringComparer.Ordinal);
		var positional = new List<BoundParameter>();
		if (primary is not null)
			positional.AddRange(primary.Parameters.Where(parameter => parameter.Symbol.Name != "$"));
		else if (type.IsValueType)
		{
			foreach (FieldSymbol field in type.Members.Fields)
				if (ReferenceEquals(field.Owner, type) && !field.IsStatic && !IsAutoBacking(field))
					fromParameters.Add(field.Name);
		}

		var parts = new List<string>();
		foreach (BoundParameter parameter in positional)
		{
			ParameterSymbol symbol = parameter.Symbol;
			if (symbol.Field is { } field)
			{
				fromParameters.Add(field.Name);
				string keyword = type.IsImmutable ? "val " : "var ";
				parts.Add($"{keyword}{MemberName(symbol.Name)}: {TypeName(symbol.Type)}");
			}
			else
			{
				parts.Add($"{MemberName(symbol.Name)}: {TypeName(symbol.Type)}");
			}
		}

		if (primary is null && type.IsValueType)
		{
			foreach (FieldSymbol field in type.Members.Fields)
			{
				if (!ReferenceEquals(field.Owner, type) || field.IsStatic || IsAutoBacking(field) ||
					fromParameters.Contains(field.Name))
					continue;
				string keyword = type.IsImmutable ? "val " : "var ";
				parts.Add($"{keyword}{MemberName(field.Name)}: {TypeName(field.Type)}");
				fromParameters.Add(field.Name);
			}
		}

		string kind = type.IsValueType
			? "data class"
			: type.IsAbstract ? "abstract class" : type.IsOpen ? "open class" : "class";
		string baseType = !type.IsValueType && type.Bases.Count > 0
			? " : " + TypeName(type.Bases[0]) + (parts.Count > 0 ? "()" : "()")
			: "";
		string head = $"{Access(type.Accessibility)}{kind} {Naming.Pascal(type.Name)}" +
			$"{GenericParameters(type.GenericParameters)}({string.Join(", ", parts)}){baseType}";
		W.Open(head);
		EmitMembers(type, fromParameters, primary);
		W.Close();
	}

	private void EmitMembers(TypeSymbol type, HashSet<string> fromParameters, BoundFunction? primary)
	{
		foreach (FieldSymbol field in type.Members.Fields)
		{
			if (!ReferenceEquals(field.Owner, type) || IsAutoBacking(field) ||
				fromParameters.Contains(field.Name))
				continue;
			if (field.IsStatic)
				continue;
			string modifier = field.IsBackingField ? "private " : "";
			W.Line($"{modifier}var {FieldMemberName(field)}: {TypeName(field.Type)} = {ZeroOrNull(field.Type)}");
		}

		foreach (PropertySymbol property in type.Members.Properties)
		{
			if (!ReferenceEquals(property.Owner, type) || property.IsExtension || property.IsStatic ||
				fromParameters.Contains(property.Name))
				continue;
			EmitProperty(
				property,
				type,
				property.Getter is { } getter ? BoundOf(getter) : null,
				property.Setter is { } setter ? BoundOf(setter) : null,
				property.Init is { } init ? BoundOf(init) : null);
		}

		if (primary?.Body is { } primaryBody && (primaryBody.Statements.Count > 0 || primaryBody.Value is not null))
		{
			BeginFunction();
			ThisAlias = "this";
			RegisterSelf(primary);
			W.Open("init");
			EmitBody(primaryBody, PrimitiveType.Void, isCtor: true);
			W.Close();
		}

		foreach (BoundFunction constructor in MembersOf(type).Where(function => function.Symbol.IsConstructor))
		{
			if (constructor.Symbol.IsPrimaryConstructor)
				continue;
			EmitConstructor(type, constructor);
			W.Line();
		}

		List<BoundFunction> methods = [.. MembersOf(type)
			.Where(function => !function.Symbol.IsConstructor && !function.Symbol.IsAccessor &&
				function.Symbol.Name != "<static-init>")];
		foreach (BoundFunction method in methods.Where(function => !function.Symbol.IsStatic))
		{
			EmitMethod(type, method);
			W.Line();
		}

		List<BoundFunction> statics = [.. methods.Where(function => function.Symbol.IsStatic)];
		if (statics.Count > 0 || type.Members.StaticInitializer is not null ||
			type.Members.Fields.Any(field => ReferenceEquals(field.Owner, type) && field.IsStatic))
		{
			W.Open("companion object");
			foreach (FieldSymbol field in type.Members.Fields)
			{
				if (ReferenceEquals(field.Owner, type) && field.IsStatic)
					W.Line($"var {FieldMemberName(field)}: {TypeName(field.Type)} = {ZeroOrNull(field.Type)}");
			}

			foreach (BoundFunction method in statics)
			{
				EmitFunction(method, type, extension: false);
				W.Line();
			}

			if (type.Members.StaticInitializer is { } initializer && BoundOf(initializer) is { } initFunction)
			{
				BeginFunction();
				ThisAlias = null;
				W.Open("init");
				EmitBody(initFunction.Body, PrimitiveType.Void);
				W.Close();
			}

			W.Close();
		}
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

	protected override void EmitField(FieldSymbol field) =>
		W.Line($"var {FieldMemberName(field)}: {TypeName(field.Type)} = {ZeroOrNull(field.Type)}");

	protected override void EmitProperty(
		PropertySymbol property,
		TypeSymbol owner,
		BoundFunction? getter,
		BoundFunction? setter,
		BoundFunction? init)
	{
		string name = MemberName(property.Name);
		string type = TypeName(property.Type);
		bool custom = !property.IsAuto && (getter?.Body.Value is not null || setter?.Body.Value is not null);
		if (!custom)
		{
			if (property.BackingField is { } backing)
				W.Line($"var {FieldMemberName(backing)}: {type} = {ZeroOrNull(property.Type)}");
			return;
		}

		W.Line($"var {name}: {type}");
		W.Level++;
		try
		{
			if (getter is not null)
			{
				BeginFunction();
				ThisAlias = "this";
				RegisterSelf(getter);
				W.Open("get()");
				EmitBody(getter.Body, getter.ReturnType);
				W.Close();
			}

			BoundFunction? writer = setter ?? init;
			if (writer is not null)
			{
				BeginFunction();
				ThisAlias = "this";
				RegisterSelf(writer);
				W.Open("set(value)");
				EmitBody(writer.Body, PrimitiveType.Void);
				W.Close();
			}
		}
		finally
		{
			W.Level--;
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
			.Select(parameter =>
				$"{NameOfSlot(parameter.Slot, parameter.Symbol.Name)}: {TypeName(parameter.Symbol.Type)}"));
		string chain = "";
		if (constructor.Chain is { } constructorChain)
		{
			bool toThis = ReferenceEquals(constructorChain.Target.Owner, type);
			chain = $" : {(toThis ? "this" : "super")}({string.Join(", ", constructorChain.Arguments.Select(Value))})";
		}
		else if (constructor.BaseCalls.Count > 0 && constructor.BaseCalls[0].Arguments.Count > 0)
		{
			chain = $" : super({string.Join(", ", constructor.BaseCalls[0].Arguments.Select(Value))})";
		}

		W.Open($"constructor({parameters}){chain}");
		if (constructor.Chain is null)
		{
			foreach (BoundFieldInitializer initializer in constructor.Initializers)
			{
				if (ReferenceEquals(initializer.Field.Owner, type) && !initializer.Field.IsStatic)
					W.Line($"this.{FieldMemberName(initializer.Field)} = {StripParens(Value(initializer.Value))}");
			}

			foreach (BoundParameter parameter in constructor.Parameters)
			{
				if (parameter.Symbol.Field is { } field && !field.IsStatic && parameter.Symbol.Name != "$")
					W.Line($"this.{FieldMemberName(field)} = {NameOfSlot(parameter.Slot, parameter.Symbol.Name)}");
			}
		}

		EmitBody(constructor.Body, PrimitiveType.Void, isCtor: true);
		W.Close();
	}
}
