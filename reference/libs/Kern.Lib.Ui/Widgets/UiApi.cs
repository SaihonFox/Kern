using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Lib.Ui.Hosting;

namespace Kern.Lib.Ui.Widgets;

/// <summary>
/// `kern.ui` — окна и элементы управления на Avalonia. Элементы создаются фабриками `ui.*`, собираются в `ui.column([...])` /
/// `ui.row([...])`, окно запускается `ui.run(w)` (блокирует до закрытия окна).
/// </summary>
[KernType("ui")]
public static class UiApi
{
	[KernStatic("window")]
	public static KernWidget MakeWindow(string title, int width, int height, KernWidget content)
	{
		var w = new KernWidget(WidgetKind.Window, title);
		w.SetSize(width, height);
		w.AddChildInternal(content);
		return w;
	}

	[KernStatic("label")] public static KernWidget MakeLabel(string text) => new(WidgetKind.Label, text);
	[KernStatic("button")] public static KernWidget MakeButton(string text) => new(WidgetKind.Button, text);
	[KernStatic("input")] public static KernWidget MakeInput(string text = "") => new(WidgetKind.Input, text);

	[KernStatic("checkbox")]
	public static KernWidget MakeCheckbox(string text, bool isChecked = false)
	{
		var w = new KernWidget(WidgetKind.CheckBox, text);
		w.SetChecked(isChecked);
		return w;
	}

	[KernStatic("slider")]
	public static KernWidget MakeSlider(double min, double max, double value)
	{
		var w = new KernWidget(WidgetKind.Slider);
		w.SetRange(min, max, value);
		return w;
	}

	[KernStatic("progress")]
	public static KernWidget MakeProgress(double min, double max, double value)
	{
		var w = new KernWidget(WidgetKind.Progress);
		w.SetRange(min, max, value);
		return w;
	}

	[KernStatic("list")]
	public static KernWidget MakeList(List<string> items)
	{
		var w = new KernWidget(WidgetKind.List);
		w.SetItemsInternal(items);
		return w;
	}

	[KernStatic("column")] public static KernWidget MakeColumn(List<KernWidget> children) => Panel(WidgetKind.Column, children);
	[KernStatic("row")] public static KernWidget MakeRow(List<KernWidget> children) => Panel(WidgetKind.Row, children);

	private static KernWidget Panel(WidgetKind kind, List<KernWidget> children)
	{
		var w = new KernWidget(kind);
		foreach (var c in children) w.AddChildInternal(c);
		return w;
	}

	/// <summary>Вызывать обработчик каждые `ms` миллисекунд (таймер интерфейса).</summary>
	[KernStatic("every")] public static void Every(int ms, KAction action) => GuiHost.Every(ms, action);

	/// <summary>Показать окно и крутить цикл событий до его закрытия. Возвращает код выхода.</summary>
	[KernStatic("run")] public static int Run(KernWidget window) => GuiHost.Run(window);
}
