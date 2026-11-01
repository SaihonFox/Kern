using Kern.Compiler.Semantics.Bound;
using Kern.Compiler.Semantics.Declarations;
using Kern.Compiler.Semantics.Types;
using Kern.Bytecode;

namespace Kern.Compiler.CodeGen;

public sealed partial class CodeGenerator
{
	/// <summary>Индексы инстанциаций generic-нативных функций: (символ, аргументы) → функция.</summary>
	private readonly Dictionary<(FunctionSymbol Symbol, string Key), int> _nativeInstances = [];

	/// <summary>
	/// Индекс функции для вызова: обычная запись из <see cref="_functionIndices"/> либо, для
	/// generic-нативной функции с аргументами типа, отдельный <c>FunctionDef</c> с
	/// <c>NativeTypeArgs</c> (reified-аргументы получает <c>[KernTypeArgs] KType[]</c>).
	/// </summary>
	private int NativeCallIndex(BoundCallExpression call)
	{
		if (call.Function.NativeKey is null || call.TypeArguments.Count == 0)
			return _functionIndices.TryGetValue(call.Function, out int plain) ? plain : -1;

		FunctionSymbol symbol = call.Function;
		string key = string.Join(",", call.TypeArguments.Select(argument => argument.ToString()));
		if (_nativeInstances.TryGetValue((symbol, key), out int existing))
			return existing;

		var substitution = new Dictionary<string, KernType>(StringComparer.Ordinal);
		for (var index = 0; index < symbol.GenericParameters.Count && index < call.TypeArguments.Count; index++)
			substitution[symbol.GenericParameters[index].Name] = call.TypeArguments[index];

		var parameterTypes = new List<TypeSig>();
		if (symbol.NativeSelfType is { } self)
			parameterTypes.Add(TypeSigMap.From(TypeSubstitution.Apply(self, substitution), _module.Name));
		foreach (ParameterSymbol parameter in symbol.Parameters)
			parameterTypes.Add(TypeSigMap.From(TypeSubstitution.Apply(parameter.Type, substitution), _module.Name));

		int typeArguments = _module.InternTypeSigList(
			[.. call.TypeArguments.Select(argument => TypeSigMap.From(argument, _module.Name))]);

		var definition = new FunctionDef
		{
			Name = symbol.Name,
			ParamTypes = [.. parameterTypes],
			ReturnType = TypeSigMap.From(
				TypeSubstitution.Apply(symbol.ReturnType ?? PrimitiveType.Void, substitution),
				_module.Name),
			LocalCount = 0,
			MaxStack = 0,
			Flags = MemberFlags.Native | MemberFlags.Static,
			NativeKey = symbol.NativeKey,
			NativeTypeArgs = typeArguments,
		};
		int functionIndex = _module.AddFunction(definition);
		_nativeInstances[(symbol, key)] = functionIndex;
		return functionIndex;
	}
}
