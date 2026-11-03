using System.Text;
using Kern.Binding.Attributes;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Text;

/// <summary>
/// Шаблонизатор <c>kern.text</c>: <c>{{key}}</c>, <c>{{#if key}}…{{else}}…{{/if}}</c>,
/// <c>{{#each items}}…{{/each}}</c> (с <c>{{this}}</c> и <c>{{@index}}</c>) и
/// <c>{{&gt; partial}}</c>. Значения по умолчанию экранируются как HTML; <c>{{&amp; key}}</c>
/// вставляет строку без экранирования.
/// </summary>
public static partial class TextModule
{
	[KernName("template_render_with")]
	[KernMember]
	public static string TemplateRenderWith(string template, KValue values, KValue partials) =>
		new TemplateRenderer(values, partials).Render(template);

	private sealed class TemplateRenderer(KValue values, KValue partials)
	{
		private readonly KDict? _values = values.Ref as KDict;
		private readonly KDict? _partials = partials.Ref as KDict;

		public string Render(string template)
		{
			if (_values is null)
				throw new KernPanic($"text.template_render: ожидался dict, получено {values.TypeName}");
			var position = 0;
			string result = RenderSection(template, ref position, values, null, out string? stop);
			if (stop is not null)
				throw new KernPanic($"text.template_render: лишний тег '{stop}'");
			return result;
		}

		private string RenderSection(
			string template,
			ref int position,
			KValue context,
			int? index,
			out string? stop)
		{
			var builder = new StringBuilder(template.Length);
			stop = null;
			while (position < template.Length)
			{
				int open = template.IndexOf("{{", position, StringComparison.Ordinal);
				if (open < 0)
				{
					builder.Append(template, position, template.Length - position);
					position = template.Length;
					break;
				}

				builder.Append(template, position, open - position);
				int close = template.IndexOf("}}", open + 2, StringComparison.Ordinal);
				if (close < 0)
				{
					builder.Append(template, open, template.Length - open);
					position = template.Length;
					break;
				}

				string tag = template[(open + 2)..close].Trim();
				position = close + 2;
				if (tag is "else" or "/if" or "/each")
				{
					stop = tag;
					return builder.ToString();
				}

				if (tag.StartsWith("#if ", StringComparison.Ordinal))
				{
					string condition = tag[4..].Trim();
					string thenText = RenderSection(template, ref position, context, index, out stop);
					string? elseText = null;
					if (stop == "else")
						elseText = RenderSection(template, ref position, context, index, out stop);
					if (stop != "/if")
						throw new KernPanic($"text.template_render: незакрытый #if {condition}");
					builder.Append(Truthy(Lookup(context, index, condition)) ? thenText : elseText ?? string.Empty);
					stop = null;
					continue;
				}

				if (tag.StartsWith("#each ", StringComparison.Ordinal))
				{
					string name = tag[6..].Trim();
					KValue source = Lookup(context, index, name);
					int bodyStart = position;
					_ = RenderSection(template, ref position, context, index, out stop);
					int? elseStart = null;
					if (stop == "else")
					{
						elseStart = position;
						_ = RenderSection(template, ref position, context, index, out stop);
					}
					if (stop != "/each")
						throw new KernPanic($"text.template_render: незакрытый #each {name}");

					var rendered = new StringBuilder();
					if (source.Ref is KList list)
					{
						if (list.Count == 0)
							rendered.Append(elseStart is { } empty ? RenderFrom(template, empty, context, index) : string.Empty);
						else
							for (var item = 0; item < list.Count; item++)
								rendered.Append(RenderFrom(template, bodyStart, list[item], item));
					}
					else if (source.Ref is KArray array)
					{
						if (array.Length == 0)
							rendered.Append(elseStart is { } empty ? RenderFrom(template, empty, context, index) : string.Empty);
						else
							for (var item = 0; item < array.Length; item++)
								rendered.Append(RenderFrom(template, bodyStart, array[item], item));
					}
					else
					{
						rendered.Append(elseStart is { } empty ? RenderFrom(template, empty, context, index) : string.Empty);
					}

					builder.Append(rendered);
					stop = null;
					continue;
				}

				if (tag.StartsWith('>'))
				{
					string name = tag[1..].Trim();
					if (_partials?.TryGet(KValue.Str(name), out KValue partial) == true &&
						partial.Kind == ValueKind.Str)
						builder.Append(RenderFrom(partial.AsStr, 0, context, index));
					continue;
				}

				if (tag.StartsWith('&'))
				{
					builder.Append(Display(Lookup(context, index, tag[1..].Trim())));
					continue;
				}

				KValue value = Lookup(context, index, tag);
				builder.Append(value.IsNilOrNone
					? "{{" + tag + "}}"
					: EscapeHtml(Display(value)));
			}

			return builder.ToString();
		}

		private string RenderFrom(string template, int start, KValue context, int? index)
		{
			int position = start;
			return RenderSection(template, ref position, context, index, out _);
		}

		private KValue Lookup(KValue context, int? index, string key)
		{
			if (key == "this")
				return context;
			if (key == "@index")
				return index is { } indexValue ? KValue.I32(indexValue) : KValue.Nil;
			if (context.Ref is KDict dict && dict.TryGet(KValue.Str(key), out KValue value))
				return value;
			if (context.Ref is KObject obj)
			{
				foreach (var field in obj.Type.Definition.Fields)
					if (string.Equals(field.Name, key, StringComparison.Ordinal))
						return obj.GetField(field.Slot);
			}
			if (context.Ref is KDict && key == "this")
				return context;
			return KValue.Nil;
		}

		private static bool Truthy(KValue value) => value.Kind switch
		{
			ValueKind.Nil or ValueKind.None or ValueKind.Void => false,
			ValueKind.Bool => value.AsBool,
			ValueKind.Str => value.AsStr.Length > 0,
			ValueKind.I8 or ValueKind.I16 or ValueKind.I32 or ValueKind.I64
				or ValueKind.U8 or ValueKind.U16 or ValueKind.U32 or ValueKind.U64 => value.AsI64 != 0,
			ValueKind.F16 or ValueKind.F32 or ValueKind.F64 => value.AsF64 != 0,
			ValueKind.Ref => value.Ref switch
			{
				KList list => list.Count > 0,
				KArray array => array.Length > 0,
				KDict dict => dict.Count > 0,
				null => false,
				_ => true,
			},
			_ => true,
		};

		private static string Display(KValue value) => value.Kind switch
		{
			ValueKind.Nil or ValueKind.None or ValueKind.Void => string.Empty,
			ValueKind.Str => value.AsStr,
			_ => value.ToString() ?? string.Empty,
		};
	}
}
