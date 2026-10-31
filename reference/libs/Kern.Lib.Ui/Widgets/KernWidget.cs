using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Lib.Ui.Hosting;

namespace Kern.Lib.Ui.Widgets;

internal enum WidgetKind { Window, Label, Button, Input, CheckBox, Slider, Progress, List, Column, Row }

/// <summary>
/// Единый тип `widget` для всех элементов (в Kern у нативных типов нет наследования): вид задаётся фабрикой `ui.button(…)`,
/// `ui.label(…)` и т. п.; свойства, неприменимые к виду (например `value` у кнопки), хранятся, но ни на что не влияют.
/// До запуска окна это модель; после — каждое изменение сразу переносится на живой контрол.
/// </summary>
[KernType("widget")]
public sealed class KernWidget
{
	internal readonly WidgetKind Kind;
	private Control? _live;
	private bool _pushing;

	private string _text = "";
	private double _value, _min, _max = 100;
	private bool _checked, _enabled = true, _visible = true;
	private double _width = double.NaN, _height = double.NaN, _margin, _spacing, _fontSize;
	private List<string> _items = [];
	private int _selected = -1;
	private readonly List<KernWidget> _children = [];
	private KAction? _onClick, _onChange, _onClose;

	internal KernWidget(WidgetKind kind, string text = "") { Kind = kind; _text = text; }

	internal bool IsWindow => Kind == WidgetKind.Window;
	internal IReadOnlyList<KernWidget> Children => _children;
	internal void AddChildInternal(KernWidget child) => _children.Add(child);
	internal void SetSize(double width, double height) { _width = width; _height = height; }
	internal void SetRange(double min, double max, double value) { _min = min; _max = max; _value = value; }
	internal void SetChecked(bool value) => _checked = value;
	internal void SetItemsInternal(List<string> items) => _items = items;

	// ---------- свойства ----------

	[KernProperty] public string Text { get => _text; set { _text = value; Push(); } }
	[KernProperty] public double Value { get => _value; set { _value = value; Push(); } }
	[KernProperty] public double Min { get => _min; set { _min = value; Push(); } }
	[KernProperty] public double Max { get => _max; set { _max = value; Push(); } }
	[KernProperty] public bool IsChecked { get => _checked; set { _checked = value; Push(); } }
	[KernProperty] public bool Enabled { get => _enabled; set { _enabled = value; Push(); } }
	[KernProperty] public bool Visible { get => _visible; set { _visible = value; Push(); } }
	[KernProperty] public double Width { get => _width; set { _width = value; Push(); } }
	[KernProperty] public double Height { get => _height; set { _height = value; Push(); } }
	[KernProperty] public double Margin { get => _margin; set { _margin = value; Push(); } }
	[KernProperty] public double Spacing { get => _spacing; set { _spacing = value; Push(); } }
	[KernProperty] public double FontSize { get => _fontSize; set { _fontSize = value; Push(); } }
	[KernProperty] public List<string> Items { get => _items; set { _items = value; Push(); } }
	[KernProperty] public int Selected { get => _selected; set { _selected = value; Push(); } }

	// ---------- обработчики ----------

	/// <summary>Клик по кнопке.</summary>
	[KernMember] public void OnClick(KAction handler) => _onClick = handler;
	/// <summary>Изменение значения: текст поля, флажок, ползунок, выбор в списке. Новое значение читается из свойств.</summary>
	[KernMember] public void OnChange(KAction handler) => _onChange = handler;
	/// <summary>Закрытие окна.</summary>
	[KernMember] public void OnClose(KAction handler) => _onClose = handler;

	// ---------- дерево ----------

	/// <summary>Добавить дочерний элемент в column/row (у окна — заменить содержимое).</summary>
	[KernMember] public void Add(KernWidget child)
	{
		if (Kind == WidgetKind.Window) { _children.Clear(); _children.Add(child); if (_live is Window w) w.Content = child.Build(); return; }
		_children.Add(child);
		if (_live is Panel p) p.Children.Add(child.Build());
	}

	[KernMember] public void Clear()
	{
		_children.Clear();
		if (_live is Panel p) p.Children.Clear();
	}

	/// <summary>Закрыть окно.</summary>
	[KernMember] public void Close() { if (_live is Window w) w.Close(); }

	// ---------- построение живого контрола ----------

	internal Control Build()
	{
		Control c = Kind switch
		{
			WidgetKind.Window => new Window(),
			WidgetKind.Label => new TextBlock(),
			WidgetKind.Button => new Button(),
			WidgetKind.Input => new TextBox(),
			WidgetKind.CheckBox => new CheckBox(),
			WidgetKind.Slider => new Slider(),
			WidgetKind.Progress => new ProgressBar(),
			WidgetKind.List => new ListBox(),
			WidgetKind.Column => new StackPanel { Orientation = Avalonia.Layout.Orientation.Vertical },
			_ => new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal },
		};
		_live = c;

		switch (c)
		{
			case Window w:
				if (_children.Count > 0) w.Content = _children[0].Build();
				w.Closed += (_, _) => GuiHost.Fire(_onClose);
				break;
			case Panel p:
				foreach (var child in _children) p.Children.Add(child.Build());
				break;
			case Button b when b is not CheckBox:
				b.Click += (_, _) => GuiHost.Fire(_onClick);
				break;
			case TextBox tb:
				tb.PropertyChanged += (_, e) =>
				{
					if (_pushing || e.Property != TextBox.TextProperty) return;
					_text = tb.Text ?? "";
					GuiHost.Fire(_onChange);
				};
				break;
			case CheckBox cb:
				cb.PropertyChanged += (_, e) =>
				{
					if (_pushing || e.Property != ToggleButton.IsCheckedProperty) return;
					_checked = cb.IsChecked == true;
					GuiHost.Fire(_onChange);
				};
				break;
			case Slider s:
				s.PropertyChanged += (_, e) =>
				{
					if (_pushing || e.Property != RangeBase.ValueProperty) return;
					_value = s.Value;
					GuiHost.Fire(_onChange);
				};
				break;
			case ListBox lb:
				lb.PropertyChanged += (_, e) =>
				{
					if (_pushing || e.Property != SelectingItemsControl.SelectedIndexProperty) return;
					_selected = lb.SelectedIndex;
					GuiHost.Fire(_onChange);
				};
				break;
		}
		Apply(c);
		return c;
	}

	/// <summary>Перенос модели на живой контрол (если он уже построен).</summary>
	private void Push()
	{
		if (_live is { } c) Apply(c);
	}

	private void Apply(Control c)
	{
		_pushing = true;
		try
		{
			c.IsEnabled = _enabled;
			c.IsVisible = _visible;
			c.Margin = new Thickness(_margin);
			c.Width = _width;
			c.Height = _height;
			if (_fontSize > 0)
			{
				if (c is TextBlock tbk) tbk.FontSize = _fontSize;
				else if (c is Avalonia.Controls.Primitives.TemplatedControl tc) tc.FontSize = _fontSize;
			}
			switch (c)
			{
				case Window w: w.Title = _text; break;
				case TextBlock t: t.Text = _text; break;
				case Button b when b is not CheckBox: b.Content = _text; break;
				case TextBox tb: if (tb.Text != _text) tb.Text = _text; break;
				case CheckBox cb: cb.Content = _text; cb.IsChecked = _checked; break;
				case Slider s:
					s.Minimum = _min; s.Maximum = _max;
					if (s.Value != _value) s.Value = _value;
					break;
				case ProgressBar pb:
					pb.Minimum = _min; pb.Maximum = _max;
					if (pb.Value != _value) pb.Value = _value;
					break;
				case ListBox lb:
					if (!ReferenceEquals(lb.ItemsSource, _items)) lb.ItemsSource = _items;
					if (lb.SelectedIndex != _selected) lb.SelectedIndex = _selected;
					break;
				case StackPanel sp: sp.Spacing = _spacing; break;
			}
		}
		finally { _pushing = false; }
	}
}
