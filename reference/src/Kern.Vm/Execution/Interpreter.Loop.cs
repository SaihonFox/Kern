using System.Text;
using Kern.Bytecode;
using Kern.Vm.Async;
using Kern.Vm.Reflection;
using Kern.Vm.Types;
using Kern.Vm.Values;

namespace Kern.Vm.Execution;

/*
 * СЕМАНТИКА ОПКОДОВ (контракт для кодогенератора). Код — int[]; переходы — абсолютные индексы в Code.
 * Каждый вызов кладёт на стек РОВНО одно значение (для void-функций — Void); оператор-выражение обязан сделать Pop.
 *  Стек вызова: [receiver?] arg0..argN-1. receiver есть у экземплярных методов/ctor/геттеров/расширений (не Static, не Lambda).
 *  Call f,argc,sigList      — прямой вызов. sigList: аргументы типа; у статического члена generic-типа первые N — аргументы класса.
 *  CallVirt f,argc,sigList  — f = любая функция цепочки Overrides; реализация берётся из vtable класса receiver.
 *  CallNative key,argc,sig  — без receiver-слота: self (если есть) — просто arg0. CallDotnet str,argc — то же по ключу из Strings.
 *  CallClosure argc         — стек: [fn, args...].  MakeClosure f,n — снимает n KCell (LdCellRef), BoundThis = $ текущего фрейма.
 *  NewObj/NewUnit t,ctor,argc,sig — стек: [args...] → объект (ctor = -1: без конструктора). Ctor сам вызывает родительские ctor/инициализаторы.
 *  LdField/StField slot     — объект снимается со стека: LdField [obj]→[v]; StField [obj, v]→[]. Слоты назначает FieldLayout.
 *  LdCell/StCell/LdCellRef i — i>=0: захват замыкания; i<0: локальная ячейка в слоте ~i (создаётся NewCell slot: [v]→[]).
 *  JmpIfNil t               — peek: прыгает, если Nil (значение остаётся). JmpIfNotNilOrNone t — peek: не nil/none → прыжок (значение остаётся), иначе pop.
 *  JmpIfNone t              — pop, прыжок если none. MatchType sig,t — peek, прыжок если НЕ экземпляр. MatchEq t — pop pattern, peek subject, прыжок если не равны.
 *  Switch n,t0..tn-1        — pop int; 0<=v<n → t[v], иначе проваливается на код после таблицы.
 *  IterInit [iterable]→[it]; IterNext end: [it]→[it,item] либо pop it и прыжок на end.   ListAdd [list,v]→[list].
 *  RangeNew flags(1=incl,2=step): [start,end(,step)]→[range].   Index [o,i]→[v]; StIndex [o,i,v]→[].   In [item,container]→[bool].
 *  Destructure count,flags  — [v] → count значений, первый элемент на вершине. flags: 0 или (позиция rest)+1 — rest собирает остаток в list.
 *  NewList elemSig,n / NewArray elemSig,n / NewDict dictSig,pairs (k,v,k,v..) / NewTuple tupleSig|-1,n.
 *  CallOp sym,arity         — пользовательский/расширяющий оператор (числа — встроенно). Add..Shr, Eq.. на нечисловых тоже ищут пользовательский оператор.
 *  Await/AwaitAll n/MakeTask — [task]→[value]; AwaitAll снимает n задач и даёт list результатов.
 *  CastType sig             — числовое приведение для числовых/char/str/bool типов, иначе проверка (при T? неудача даёт nil, иначе panic).
 */
public sealed partial class Interpreter
{
	/// <summary>
	/// Исполняет фрейм baseFrame до его возврата. Если async-фрейм ждёт незавершённую задачу — паркуется,
	/// parked = true. Вложенные вызовы Kern-функций исполняются в этом же цикле (стек фреймов в куче).
	/// </summary>
	internal KValue Execute(Frame baseFrame, out bool parked)
	{
		parked = false;
		if (++_nativeDepth > MaxNativeDepth)
		{
			_nativeDepth--;
			throw new KernPanic("слишком глубокая вложенность нативных вызовов");
		}
		var prevCurrent = _current;
		_current = this;
		var funcs = _funcs;
		var frame = baseFrame;
		int[] code = frame.Code;
		KValue[] regs = frame.Regs;
		int pc = frame.Pc, sp = frame.Sp, lb = frame.ArgSlots;
		try
		{
			while (true)
			{
				var op = (OpCode)code[pc++];
				switch (op)
				{
					case OpCode.Nop: break;
					case OpCode.Line: frame.Line = code[pc++]; break;

					// ---- стек и константы ----
					case OpCode.SaveSp: regs[lb + code[pc++]] = KValue.I32(sp); break;
					case OpCode.RestoreSp: sp = regs[lb + code[pc++]].AsI32; break;
					case OpCode.Pop: sp--; break;
					case OpCode.Dup: regs[sp] = regs[sp - 1]; sp++; break;
					case OpCode.Swap: (regs[sp - 1], regs[sp - 2]) = (regs[sp - 2], regs[sp - 1]); break;
					case OpCode.LdConst:
					{
						int ci = code[pc++];
						if (Module.Constants[ci] is TypeSig ts)
							regs[sp++] = KValue.Obj(new KTypeObject(Types.Resolve(ts, frame.ClassArgs, frame.MethodArgs)));
						else regs[sp++] = Info.ConstValues[ci];
						break;
					}
					case OpCode.LdNil: regs[sp++] = KValue.Nil; break;
					case OpCode.LdNone: regs[sp++] = KValue.None; break;
					case OpCode.LdTrue: regs[sp++] = KValue.True; break;
					case OpCode.LdFalse: regs[sp++] = KValue.False; break;
					case OpCode.LdVoid: regs[sp++] = KValue.Void; break;
					case OpCode.LdI32: regs[sp++] = KValue.I32(code[pc++]); break;
					case OpCode.LdStr: regs[sp++] = KValue.Str(Module.Strings[code[pc++]]); break;

					// ---- локальные, аргументы, ячейки ----
					case OpCode.LdLoc: regs[sp++] = regs[lb + code[pc++]]; break;
					case OpCode.StLoc: regs[lb + code[pc++]] = regs[--sp]; break;
					case OpCode.LdArg: regs[sp++] = regs[code[pc++]]; break;
					case OpCode.StArg: regs[code[pc++]] = regs[--sp]; break;
					case OpCode.NewCell: regs[lb + code[pc++]] = KValue.Obj(new KCell(regs[--sp])); break;
					case OpCode.LdCell:
					{
						int c = code[pc++];
						regs[sp++] = (c >= 0 ? frame.Captures[c] : (KCell)regs[lb + ~c].Ref!).Value;
						break;
					}
					case OpCode.StCell:
					{
						int c = code[pc++];
						var cell = c >= 0 ? frame.Captures[c] : (KCell)regs[lb + ~c].Ref!;
						cell.Value = regs[--sp];
						break;
					}
					case OpCode.LdCellRef:
					{
						int c = code[pc++];
						regs[sp++] = KValue.Obj(c >= 0 ? frame.Captures[c] : (KCell)regs[lb + ~c].Ref!);
						break;
					}

					// ---- поля, статика ----
					case OpCode.LdThis: regs[sp++] = frame.This; break;
					case OpCode.LdField:
					{
						int slot = code[pc++];
						var o = regs[--sp];
						if (o.Ref is KObject ko) regs[sp++] = ko.Fields[slot];
						else throw NilAccess(o, "поле");
						break;
					}
					case OpCode.StField:
					{
						int slot = code[pc++];
						var v = regs[--sp];
						var o = regs[--sp];
						if (o.Ref is KObject ko) ko.Fields[slot] = v;
						else throw NilAccess(o, "поле");
						break;
					}
					case OpCode.LdStatic: regs[sp++] = Info.Statics[code[pc++]]; break;
					case OpCode.StStatic: Info.Statics[code[pc++]] = regs[--sp]; break;
					case OpCode.GetProp:
					{
						string name = Module.Strings[code[pc++]];
						frame.Pc = pc; frame.Sp = sp;
						regs[sp - 1] = GetPropDynamic(regs[sp - 1], name);
						break;
					}
					case OpCode.SetProp:
					{
						string name = Module.Strings[code[pc++]];
						var v = regs[--sp];
						var o = regs[--sp];
						frame.Pc = pc; frame.Sp = sp;
						SetPropDynamic(o, name, v);
						break;
					}

					// ---- вызовы ----
					case OpCode.Call:
					{
						int fi = code[pc++], argc = code[pc++], sl = code[pc++];
						var fd = funcs[fi];
						frame.Pc = pc;
						int start = sp - argc;
						KValue self = default;
						bool hasSelf = false;
						if (_isInstance[fi])
						{
							self = regs[start - 1];
							hasSelf = true;
							sp = start - 1;
							if (self.IsNilOrNone) throw NilAccess(self, "метод " + fd.Name);
						}
						else sp = start;
						frame.Sp = sp;
						CallTypeArgs(fd, hasSelf, self, sl, frame, out var ca, out var ma);
						var nf = Enter(frame, fi, fd, regs, start, argc, self, hasSelf, [], ca, ma, out var res);
						if (nf is null) { regs[sp++] = res; break; }
						frame = nf; code = frame.Code; regs = frame.Regs; pc = 0; sp = frame.Sp; lb = frame.ArgSlots;
						break;
					}
					case OpCode.CallVirt:
					{
						int fi = code[pc++], argc = code[pc++], sl = code[pc++];
						frame.Pc = pc;
						int start = sp - argc;
						var self = regs[start - 1];
						sp = start - 1;
						frame.Sp = sp;
						if (self.IsNilOrNone) throw NilAccess(self, "метод " + funcs[fi].Name);
						var ko = self.Ref as KObject;
						int impl = ko is null ? fi : ResolveVirtual(ko, fi);
						if (impl < 0)
						{
							if (ko!.Native is KProxyState ps)
							{
								var pargs = new KValue[argc];
								Array.Copy(regs, start, pargs, 0, argc);
								regs[sp++] = ps.Invoke(this, fi, pargs);
								break;
							}
							throw new KernPanic($"метод {funcs[fi].Name} не реализован в {ko.Type.Name}");
						}
						var ifd = funcs[impl];
						CallTypeArgs(ifd, true, self, sl, frame, out var ca, out var ma);
						var nf = Enter(frame, impl, ifd, regs, start, argc, self, true, [], ca, ma, out var res);
						if (nf is null) { regs[sp++] = res; break; }
						frame = nf; code = frame.Code; regs = frame.Regs; pc = 0; sp = frame.Sp; lb = frame.ArgSlots;
						break;
					}
					case OpCode.CallNative:
					{
						string key = Module.Strings[code[pc++]];
						int argc = code[pc++], sl = code[pc++];
						frame.Pc = pc; frame.Sp = sp;
						var targs = ResolveList(sl, frame.ClassArgs, frame.MethodArgs);
						var r = GetNative(key)(this, new ReadOnlySpan<KValue>(regs, sp - argc, argc), targs);
						sp -= argc;
						regs[sp++] = r;
						break;
					}
					case OpCode.CallDotnet:
					{
						string key = Module.Strings[code[pc++]];
						int argc = code[pc++];
						frame.Pc = pc; frame.Sp = sp;
						var r = GetNative(key)(this, new ReadOnlySpan<KValue>(regs, sp - argc, argc), []);
						sp -= argc;
						regs[sp++] = r;
						break;
					}
					case OpCode.CallClosure:
					{
						int argc = code[pc++];
						frame.Pc = pc;
						int start = sp - argc;
						var fv = regs[start - 1];
						sp = start - 1;
						frame.Sp = sp;
						if (fv.Ref is not KFunction kf) throw new KernPanic(fv.IsNilOrNone ? "вызов nil как функции" : "значение не является функцией: " + ValueOps.ToStr(fv));
						if (kf.Delegate is { } del)
						{
							var dargs = new KValue[argc];
							Array.Copy(regs, start, dargs, 0, argc);
							regs[sp++] = del(dargs);
							break;
						}
						int fi = FuncIndexOf(kf);
						KValue self = kf.BoundThis;
						bool hasSelf = kf.HasThis;
						int s = start, n = argc;
						if (_isInstance[fi] && !hasSelf) { self = regs[start]; hasSelf = true; s++; n--; }
						var nf = Enter(frame, fi, funcs[fi], regs, s, n, self, hasSelf, kf.Captures, kf.ClassArgs, kf.MethodArgs, out var res);
						if (nf is null) { regs[sp++] = res; break; }
						frame = nf; code = frame.Code; regs = frame.Regs; pc = 0; sp = frame.Sp; lb = frame.ArgSlots;
						break;
					}
					case OpCode.Ret:
					case OpCode.RetVoid:
					{
						var rv = op == OpCode.Ret ? regs[--sp] : KValue.Void;
						if (frame.ReturnsThis) rv = frame.This;
						if (ReferenceEquals(frame, baseFrame)) return rv;
						frame = frame.Caller!;
						code = frame.Code; regs = frame.Regs; pc = frame.Pc; sp = frame.Sp; lb = frame.ArgSlots;
						regs[sp++] = rv;
						break;
					}

					// ---- объекты и коллекции ----
					case OpCode.NewObj:
					case OpCode.NewUnit:
					{
						int ti = code[pc++], ci = code[pc++], argc = code[pc++], sl = code[pc++];
						frame.Pc = pc;
						var cargs = ResolveList(sl, frame.ClassArgs, frame.MethodArgs);
						var ct = Types.Class(ti, cargs);
						var obj = new KObject(ct, new KValue[Info.Classes[ti].FieldCount]);
						int start = sp - argc;
						sp = start;
						frame.Sp = sp;
						if (ci < 0) { regs[sp++] = KValue.Obj(obj); break; }
						var cfd = funcs[ci];
						if (cfd.Flags.HasFlag(MemberFlags.Native))
						{
							var r = GetNative(cfd.NativeKey!)(this, new ReadOnlySpan<KValue>(regs, start, argc), cargs);
							obj.Native = Num.ToClr(r);
							regs[sp++] = KValue.Obj(obj);
							break;
						}
						frame = NewFrame(cfd, ci, regs, start, argc, KValue.Obj(obj), true, [], cargs, [], frame, returnsThis: true);
						code = frame.Code; regs = frame.Regs; pc = 0; sp = frame.Sp; lb = frame.ArgSlots;
						break;
					}
					case OpCode.CopyUnit: regs[sp - 1] = CopyUnitValue(regs[sp - 1]); break;
					case OpCode.NewList:
					{
						var et = ResolveSig(code[pc++], frame.ClassArgs, frame.MethodArgs);
						int n = code[pc++];
						var list = new List<KValue>(n);
						int start = sp - n;
						for (int i = 0; i < n; i++) list.Add(regs[start + i]);
						sp = start;
						regs[sp++] = KValue.Obj(new KListObject(et, list));
						break;
					}
					case OpCode.NewArray:
					{
						var et = ResolveSig(code[pc++], frame.ClassArgs, frame.MethodArgs);
						int n = code[pc++];
						var arr = new KValue[n];
						sp -= n;
						Array.Copy(regs, sp, arr, 0, n);
						regs[sp++] = KValue.Obj(new KArrayObject(et, arr));
						break;
					}
					case OpCode.NewDict:
					{
						var dt = ResolveSig(code[pc++], frame.ClassArgs, frame.MethodArgs);
						int n = code[pc++];
						var (kt, vt) = dt is KBuiltinGenericType { Args.Length: 2 } g ? (g.Args[0], g.Args[1]) : (TypeFactory.Object, TypeFactory.Object);
						var d = new KDictObject(kt, vt);
						int start = sp - 2 * n;
						for (int i = 0; i < n; i++) d.Items[regs[start + 2 * i]] = regs[start + 2 * i + 1];
						sp = start;
						regs[sp++] = KValue.Obj(d);
						break;
					}
					case OpCode.NewTuple:
					{
						int sig = code[pc++];
						int n = code[pc++];
						string?[]? names = sig >= 0 && Module.TypeSigs[sig] is TypeSig.Tuple { Names: { } nn } ? nn.ToArray() : null;
						var items = new KValue[n];
						sp -= n;
						Array.Copy(regs, sp, items, 0, n);
						regs[sp++] = KValue.Obj(new KTupleObject(items, names));
						break;
					}
					case OpCode.MakeClosure:
					{
						int fi = code[pc++], n = code[pc++];
						var caps = new KCell[n];
						sp -= n;
						for (int i = 0; i < n; i++) caps[i] = (KCell)regs[sp + i].Ref!;
						regs[sp++] = KValue.Obj(new KFunction
						{
							Def = funcs[fi], FuncIndex = fi, Captures = caps,
							BoundThis = frame.This, HasThis = frame.HasThis,
							ClassArgs = frame.ClassArgs, MethodArgs = frame.MethodArgs,
						});
						break;
					}
					case OpCode.LdFunc:
					{
						int fi = code[pc++];
						regs[sp++] = KValue.Obj(new KFunction { Def = funcs[fi], FuncIndex = fi, ClassArgs = frame.ClassArgs, MethodArgs = frame.MethodArgs });
						break;
					}
					case OpCode.BindMethod:
					{
						string name = Module.Strings[code[pc++]];
						regs[sp - 1] = BindMethodDynamic(regs[sp - 1], name);
						break;
					}

					// ---- арифметика и сравнения ----
					case OpCode.Add: case OpCode.Sub: case OpCode.Mul: case OpCode.Div: case OpCode.Mod: case OpCode.Pow:
					case OpCode.BitAnd: case OpCode.BitOr: case OpCode.BitXor: case OpCode.Shl: case OpCode.Shr:
					{
						var b = regs[--sp];
						var a = regs[sp - 1];
						if (op == OpCode.Add && a.Kind == ValueKind.I32 && b.Kind == ValueKind.I32) regs[sp - 1] = KValue.I32((int)a.Bits + (int)b.Bits);
						else { frame.Pc = pc; frame.Sp = sp; regs[sp - 1] = Binary(op, a, b); }
						break;
					}
					case OpCode.Neg: case OpCode.BitNot:
						frame.Pc = pc; frame.Sp = sp;
						regs[sp - 1] = Unary(op, regs[sp - 1]);
						break;
					case OpCode.Eq: case OpCode.Ne: case OpCode.Lt: case OpCode.Gt: case OpCode.Le: case OpCode.Ge:
					{
						var b = regs[--sp];
						var a = regs[sp - 1];
						if (a.Kind == ValueKind.I32 && b.Kind == ValueKind.I32)
						{
							int x = (int)a.Bits, y = (int)b.Bits;
							regs[sp - 1] = KValue.Bool(op switch
							{
								OpCode.Eq => x == y, OpCode.Ne => x != y, OpCode.Lt => x < y,
								OpCode.Gt => x > y, OpCode.Le => x <= y, _ => x >= y,
							});
						}
						else { frame.Pc = pc; frame.Sp = sp; regs[sp - 1] = KValue.Bool(CompareOp(op, a, b)); }
						break;
					}
					case OpCode.RefEq: { var b = regs[--sp]; regs[sp - 1] = KValue.Bool(RefEquals(regs[sp - 1], b)); break; }
					case OpCode.RefNe: { var b = regs[--sp]; regs[sp - 1] = KValue.Bool(!RefEquals(regs[sp - 1], b)); break; }
					case OpCode.Not: regs[sp - 1] = KValue.Bool(!regs[sp - 1].AsBool); break;
					case OpCode.CallOp:
					{
						string sym = Module.Strings[code[pc++]];
						int arity = code[pc++];
						frame.Pc = pc; frame.Sp = sp;
						var args = new KValue[arity];
						sp -= arity;
						Array.Copy(regs, sp, args, 0, arity);
						regs[sp++] = CallOperator(sym, args);
						break;
					}
					case OpCode.Concat:
					{
						int n = code[pc++];
						frame.Pc = pc; frame.Sp = sp;
						sp -= n;
						if (n == 2) regs[sp] = KValue.Str(string.Concat(ValueOps.ToStr(regs[sp]), ValueOps.ToStr(regs[sp + 1])));
						else
						{
							var sb = new StringBuilder();
							for (int i = 0; i < n; i++) sb.Append(ValueOps.ToStr(regs[sp + i]));
							regs[sp] = KValue.Str(sb.ToString());
						}
						sp++;
						break;
					}
					case OpCode.Format:
					{
						string spec = Module.Strings[code[pc++]];
						frame.Pc = pc; frame.Sp = sp;
						regs[sp - 1] = KValue.Str(FormatValue(regs[sp - 1], spec));
						break;
					}

					// ---- типы ----
					case OpCode.IsType:
						regs[sp - 1] = KValue.Bool(IsInstance(regs[sp - 1], ResolveSig(code[pc++], frame.ClassArgs, frame.MethodArgs)));
						break;
					case OpCode.NotIsType:
						regs[sp - 1] = KValue.Bool(!IsInstance(regs[sp - 1], ResolveSig(code[pc++], frame.ClassArgs, frame.MethodArgs)));
						break;
					case OpCode.CastType:
						frame.Pc = pc; frame.Sp = sp;
						regs[sp - 1] = CastValue(regs[sp - 1], ResolveSig(code[pc++], frame.ClassArgs, frame.MethodArgs));
						break;
					case OpCode.TypeOf:
						regs[sp++] = KValue.Obj(new KTypeObject(ResolveSig(code[pc++], frame.ClassArgs, frame.MethodArgs)));
						break;

					// ---- переходы ----
					case OpCode.Jmp: pc = code[pc]; break;
					case OpCode.JmpIf: { int t = code[pc++]; if (regs[--sp].AsBool) pc = t; break; }
					case OpCode.JmpIfNot: { int t = code[pc++]; if (!regs[--sp].AsBool) pc = t; break; }
					case OpCode.JmpIfNil: { int t = code[pc++]; if (regs[sp - 1].IsNil) pc = t; break; }
					case OpCode.JmpIfNotNilOrNone: { int t = code[pc++]; if (!regs[sp - 1].IsNilOrNone) pc = t; else sp--; break; }
					case OpCode.JmpIfNone: { int t = code[pc++]; if (regs[--sp].IsNone) pc = t; break; }
					case OpCode.Switch:
					{
						int n = code[pc++];
						long v = regs[--sp].Bits;
						if ((ulong)v < (ulong)n) pc = code[pc + (int)v]; else pc += n;
						break;
					}
					case OpCode.MatchType:
					{
						int sig = code[pc++], t = code[pc++];
						if (!IsInstance(regs[sp - 1], ResolveSig(sig, frame.ClassArgs, frame.MethodArgs))) pc = t;
						break;
					}
					case OpCode.MatchEq:
					{
						int t = code[pc++];
						var pat = regs[--sp];
						frame.Pc = pc; frame.Sp = sp;
						if (!CompareOp(OpCode.Eq, regs[sp - 1], pat)) pc = t;
						break;
					}

					// ---- итерация, диапазоны, индексация ----
					case OpCode.IterInit:
						frame.Pc = pc; frame.Sp = sp;
						regs[sp - 1] = KValue.Obj(MakeIterator(regs[sp - 1]));
						break;
					case OpCode.IterNext:
					{
						int end = code[pc++];
						frame.Pc = pc; frame.Sp = sp;
						var it = (KIterator)regs[sp - 1].Ref!;
						if (it.MoveNext()) regs[sp++] = it.Current;
						else { sp--; pc = end; }
						break;
					}
					case OpCode.RangeNew:
					{
						int flags = code[pc++];
						var step = (flags & 2) != 0 ? regs[--sp] : KValue.None;
						var end = regs[--sp];
						var start = regs[--sp];
						regs[sp++] = KValue.Obj(new KRange(start, end, step, (flags & 1) != 0));
						break;
					}
					case OpCode.ListAdd:
					{
						var v = regs[--sp];
						((KListObject)regs[sp - 1].Ref!).Items.Add(v);
						break;
					}
					case OpCode.Index:
					{
						var i = regs[--sp];
						frame.Pc = pc; frame.Sp = sp;
						regs[sp - 1] = IndexGet(regs[sp - 1], i);
						break;
					}
					case OpCode.StIndex:
					{
						var v = regs[--sp];
						var i = regs[--sp];
						var o = regs[--sp];
						frame.Pc = pc; frame.Sp = sp;
						IndexSet(o, i, v);
						break;
					}
					case OpCode.In:
					case OpCode.NotIn:
					{
						var c = regs[--sp];
						frame.Pc = pc; frame.Sp = sp;
						bool r = Contains(regs[sp - 1], c);
						regs[sp - 1] = KValue.Bool(op == OpCode.In ? r : !r);
						break;
					}
					case OpCode.Destructure:
					{
						int count = code[pc++], flags = code[pc++];
						var v = regs[--sp];
						frame.Pc = pc; frame.Sp = sp;
						sp = DestructureInto(v, count, flags, regs, sp);
						break;
					}

					// ---- async ----
					case OpCode.MakeTask:
						regs[sp - 1] = regs[sp - 1].Ref is KTask ? regs[sp - 1] : KValue.Obj(KTask.FromResult(regs[sp - 1]));
						break;
					case OpCode.AwaitAll:
					{
						int n = code[pc++];
						sp -= n;
						var tasks = new List<KTask>(n);
						for (int i = 0; i < n; i++)
							tasks.Add(regs[sp + i].Ref as KTask ?? KTask.FromResult(regs[sp + i]));
						regs[sp++] = KValue.Obj(KTask.All(tasks, TypeFactory.Object));
						goto case OpCode.Await;
					}
					case OpCode.Await:
					{
						var tv = regs[--sp];
						if (tv.Ref is not KTask task) { regs[sp++] = tv; break; }
						if (task.IsCompleted) { regs[sp++] = task.Unwrap(); break; }
						if (!ReferenceEquals(frame, baseFrame) || frame.Task is null)
							throw new KernPanic("await допустим только в async-функции");
						frame.Pc = pc; frame.Sp = sp;
						var co = frame.Task;
						co.Parked = frame;
						task.OnCompleted(done => ResumeCoroutine(co, done));
						parked = true;
						return default;
					}

					// ---- прочее ----
					case OpCode.Panic:
					{
						var v = regs[--sp];
						frame.Pc = pc; frame.Sp = sp;
						var p = new KernPanic(v.Kind == ValueKind.Str ? v.AsStr : ValueOps.ToStr(v)) { Payload = v };
						throw p;
					}
					case OpCode.AssertNonNil:
						if (regs[sp - 1].IsNilOrNone) throw new KernPanic("значение nil там, где оно утверждалось непустым (!!)");
						break;
					case OpCode.NewResult:
						regs[sp - 1] = KValue.Obj(new KResultObject(code[pc++] != 0, regs[sp - 1]));
						break;
					case OpCode.ResultIsOk:
						regs[sp - 1] = KValue.Bool(((KResultObject)regs[sp - 1].Ref!).IsOk);
						break;
					case OpCode.ResultValue:
						regs[sp - 1] = ((KResultObject)regs[sp - 1].Ref!).Value;
						break;
					case OpCode.CountIs:
					{
						int n = code[pc++], atLeast = code[pc++];
						int len = LengthOf(regs[sp - 1]);
						regs[sp - 1] = KValue.Bool(len >= 0 && (atLeast != 0 ? len >= n : len == n));
						break;
					}
					case OpCode.BindFunc:
					{
						int fi = code[pc++];
						regs[sp - 1] = KValue.Obj(new KFunction
						{
							Def = funcs[fi], FuncIndex = fi, BoundThis = regs[sp - 1], HasThis = true,
							ClassArgs = frame.ClassArgs, MethodArgs = frame.MethodArgs,
						});
						break;
					}
					case OpCode.LdEnum:
					{
						int ti = code[pc++], mi = code[pc++];
						regs[sp++] = EnumValue(ti, mi);
						break;
					}
					default:
						throw new KernPanic($"неизвестный опкод {op} ({(int)op}) в {FrameName(frame.Def)}");
				}
			}
		}
		catch (KernExit) { throw; }
		catch (Exception e)
		{
			var p = ToPanic(e);
			if (!p.TraceFilled) { FillTrace(p, frame, pc); p.TraceFilled = true; }
			if (ReferenceEquals(p, e)) throw;
			throw p;
		}
		finally
		{
			_current = prevCurrent;
			_nativeDepth--;
		}
	}

	private static KernPanic NilAccess(KValue v, string what) =>
		new(v.IsNilOrNone ? $"обращение к {what} через nil" : $"{what}: значение {v.Kind} не является объектом");

	private int FuncIndexOf(KFunction kf) => kf.FuncIndex >= 0 ? kf.FuncIndex : Module.Functions.IndexOf(kf.Def!);

	/// <summary>Аргументы типа для вызова: класс (из receiver или первые N у статического члена generic-типа) и метод.</summary>
	private void CallTypeArgs(FunctionDef fd, bool hasSelf, KValue self, int sl, Frame frame, out KType[] ca, out KType[] ma)
	{
		ca = [];
		var list = sl >= 0 ? ResolveList(sl, frame.ClassArgs, frame.MethodArgs) : [];
		int ownerGen = fd.Owner >= 0 ? Module.Types[fd.Owner].GenericParams.Count : 0;
		if (hasSelf)
		{
			if (ownerGen > 0 && self.Ref is KObject so) ca = TypeChecks.AncestorArgs(so.Type, fd.Owner) ?? [];
			ma = list;
		}
		else if (ownerGen > 0 && !fd.Flags.HasFlag(MemberFlags.Lambda) && list.Length >= ownerGen)
		{
			ca = list[..ownerGen];
			ma = list[ownerGen..];
		}
		else ma = list;
	}
}
