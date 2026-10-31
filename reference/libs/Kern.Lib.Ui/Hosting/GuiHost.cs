using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Kern.Lib.Ui.Widgets;
using Kern.Vm.Execution;
using Kern.Binding.Wrappers;

namespace Kern.Lib.Ui.Hosting;

/// <summary>
/// Хост Avalonia. Дерево виджетов Kern (<see cref="KernWidget"/>) — только модель; настоящие контролы строятся здесь, уже
/// после старта приложения, и затем синхронизируются при изменении свойств. Цикл событий крутится внутри нативного вызова
/// `ui.run`, поэтому обработчики Kern выполняются в том же потоке VM.
/// </summary>
internal static class GuiHost
{
	private static KernWidget? _root;
	private static bool _ran;
	private static bool _running;
	private static readonly List<(int Ms, KAction Action)> PendingTimers = [];

	public static KernWidget? Root => _root;

	public static int Run(KernWidget window)
	{
		if (_ran) throw new KernPanic("ui.run можно вызвать только один раз за запуск программы");
		if (!window.IsWindow) throw new KernPanic("ui.run ожидает окно (`ui.window(…)`)");
		_ran = true;
		_root = window;
		return AppBuilder.Configure<KernApp>().UsePlatformDetect().StartWithClassicDesktopLifetime([]);
	}

	/// <summary>Периодический вызов; до `ui.run` — откладывается до старта, после — стартует сразу.</summary>
	public static void Every(int ms, KAction action)
	{
		if (_running) StartTimer(ms, action);
		else PendingTimers.Add((ms, action));
	}

	internal static void OnStarted()
	{
		_running = true;
		foreach (var (ms, action) in PendingTimers) StartTimer(ms, action);
		PendingTimers.Clear();
	}

	private static void StartTimer(int ms, KAction action)
	{
		var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1, ms)) };
		timer.Tick += (_, _) => Fire(action);
		timer.Start();
	}

	/// <summary>Вызов обработчика Kern; паника в обработчике печатается и не роняет окно.</summary>
	public static void Fire(KAction? action)
	{
		if (action is not { } a) return;
		try { a.Invoke(); }
		catch (KernPanic p) { Console.Error.WriteLine(p.Format()); }
	}
}

/// <summary>Приложение Avalonia: тема Fluent и главное окно из корневого виджета.</summary>
internal sealed class KernApp : Application
{
	public override void Initialize() => Styles.Add(new FluentTheme());

	public override void OnFrameworkInitializationCompleted()
	{
		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && GuiHost.Root is { } root)
		{
			desktop.MainWindow = (Window)root.Build();
			GuiHost.OnStarted();
		}
		base.OnFrameworkInitializationCompleted();
	}
}
