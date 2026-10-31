using System.Xml;
using System.Xml.Linq;
using Kern.Binding.Attributes;
using Kern.Binding.Wrappers;
using Kern.Runtime.Core;
using Kern.Vm.Values;

namespace Kern.Runtime.Serialization.Xml;

/// <summary>XML-элемент: <c>xml.parse(text)</c> → <c>node</c>; <c>n.attr("id")</c>, <c>n.children</c>, <c>n.find_all("item")</c>.</summary>
[KernType("xml_node")]
public sealed class XmlNode(XElement element)
{
	[KernProperty] public string Name => element.Name.LocalName;
	/// <summary>Текст внутри элемента (без вложенных элементов).</summary>
	[KernProperty] public string Text => string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value)).Trim();
	[KernProperty] public KList<XmlNode> Children => Kv.List<XmlNode>(element.Elements().Select(e => KValue.Obj(new XmlNode(e))));
	[KernProperty] public KList<string> AttrNames => Kv.Strs(element.Attributes().Select(a => a.Name.LocalName));

	[KernMember] public string? Attr(string name) => element.Attribute(name)?.Value;
	[KernMember] public XmlNode? Child(string name) => element.Element(name) is { } e ? new XmlNode(e) : null;
	[KernMember] public KList<XmlNode> FindAll(string name) =>
		Kv.List<XmlNode>(element.Descendants(name).Select(e => KValue.Obj(new XmlNode(e))));

	[KernMember] public void SetAttr(string name, string value) => element.SetAttributeValue(name, value);
	[KernMember] public void SetText(string text) => element.Value = text;
	[KernMember] public XmlNode Add(string name, string text = "")
	{
		var e = new XElement(name, text);
		element.Add(e);
		return new XmlNode(e);
	}

	[KernMember] public string ToStr(bool pretty = true) => element.ToString(pretty ? SaveOptions.None : SaveOptions.DisableFormatting);
}

[KernType("xml")]
public static class XmlApi
{
	[KernStatic] public static KResult<XmlNode, string> Parse(string text)
	{
		try { return Kv.Ok<XmlNode>(KValue.Obj(new XmlNode(XDocument.Parse(text).Root!))); }
		catch (XmlException e) { return Kv.Err<XmlNode>(e.Message); }
	}

	[KernStatic("new")] public static XmlNode New(string rootName) => new(new XElement(rootName));
}
