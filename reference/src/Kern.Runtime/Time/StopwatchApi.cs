using System.Diagnostics;
using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;

namespace Kern.Runtime.Time;

/// <summary>Секундомер: <c>stopwatch sw()</c> запускается сразу; <c>sw.elapsed</c> — duration; <c>sw.stop()</c>, <c>sw.restart()</c>.</summary>
[KernType("stopwatch")]
public sealed class KernStopwatch
{
	private readonly Stopwatch _sw = Stopwatch.StartNew();

	[KernCtor] public KernStopwatch() { }

	[KernProperty] public KDuration Elapsed => new(_sw.Elapsed);
	[KernProperty] public bool IsRunning => _sw.IsRunning;
	[KernMember] public void Start() => _sw.Start();
	[KernMember] public void Stop() => _sw.Stop();
	[KernMember] public void Reset() => _sw.Reset();
	/// <summary>Обнулить и запустить заново; возвращает прошедшее до сброса время.</summary>
	[KernMember] public KDuration Restart()
	{
		var before = _sw.Elapsed;
		_sw.Restart();
		return new KDuration(before);
	}
}
