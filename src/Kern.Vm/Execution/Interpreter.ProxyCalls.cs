using Kern.Bytecode;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Vm.Execution;

public sealed partial class Interpreter
{
	/// <summary>
	/// Вызов абстрактного метода у прокси-объекта (mock/http_client.proxy): состояние
	/// объекта (<see cref="KObject.Native"/>) получает метод и аргументы.
	/// </summary>
	private bool TryInvokeProxy(FunctionDef callee, int argc, ref OperandStack stack)
	{
		if ((callee.Flags & MemberFlags.Abstract) == 0)
			return false;

		KValue receiver = argc > 0 ? stack.At(stack.Count - argc) : KValue.None;
		if (receiver.Ref is KObject { Native: IKProxyHandler handler } proxy)
		{
			KValue[] arguments = PopItems(ref stack, argc);
			stack.Push(handler.Invoke(this, proxy, callee, arguments));
			return true;
		}

		throw new KernPanic($"абстрактный метод '{callee.Name}' вызван у объекта без реализации");
	}

	/// <summary>
	/// Async-функция возвращает задачу сразу: тело выполняется отдельным состоянием до
	/// первого <c>await</c> (spec round 23), вызывающий продолжает работу.
	/// </summary>
	private bool TryStartAsyncCall(FunctionDef callee, int argc, ref OperandStack stack)
	{
		if ((callee.Flags & MemberFlags.Async) == 0)
			return false;

		KValue[] arguments = PopItems(ref stack, argc);
		stack.Push(KValue.Obj(StartAsync(KValue.Obj(callee), arguments)));
		return true;
	}

	/// <summary><c>NewObjectGeneric</c>: объект generic-типа с reified-аргументами.</summary>
	private void PushNewObjectGeneric(int typeIndex, int signatureListIndex, ref OperandStack stack)
	{
		TypeDef definition = _module.TypePool[typeIndex];
		int[] signatureIds = _module.TypeSigListPool[signatureListIndex];
		KType[] arguments = new KType[signatureIds.Length];
		for (int index = 0; index < signatureIds.Length; index++)
			arguments[index] = _types.FromSig(_module.TypeSigPool[signatureIds[index]]);

		KClassType objectType = _types.Named(definition, arguments);
		KValue[] fields = new KValue[definition.Fields.Count];
		Array.Fill(fields, KValue.None);
		stack.Push(KValue.Obj(new KObject(objectType, fields)));
	}
}
