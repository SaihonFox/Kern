using System.Xml;
using System.Xml.Linq;
using Kern.Binding.Attributes;
using Kern.Binding.Values;
using Kern.Vm.Execution;
using Kern.Vm.Values;

namespace Kern.Runtime.Xml;

/// <summary>
/// <c>kern.xml</c> (spec §18): разбор XML через <see cref="XElement"/> и нативный handle-тип
/// <c>xml_node</c> с членами <c>name/text/children/attr/child/find_all/add</c>.
/// </summary>
[KernType("xml_node")]
public sealed class KernXmlNode
{
	internal KernXmlNode(XElement element) => Element = element;

	internal XElement Element { get; }
}

/// <summary>Члены <c>xml_node</c>; первый параметр — <c>self</c>.</summary>
[KernType("xml_node")]
public static class XmlNodeMembers
{
	/// <summary><c>name</c>: имя элемента.</summary>
	[KernProperty]
	public static string Name(KNativeBox self) => Require(self).Element.Name.LocalName;

	/// <summary><c>text</c>: текстовое содержимое элемента.</summary>
	[KernProperty]
	public static string Text(KNativeBox self) => Require(self).Element.Value;

	/// <summary><c>children</c>: дочерние элементы.</summary>
	[KernProperty]
	public static KList<KernXmlNode> Children(KNativeBox self) =>
		Nodes(Require(self).Element.Elements());

	/// <summary><c>attr(name)</c>: значение атрибута или <c>nil</c>.</summary>
	[KernMember]
	public static string? Attr(KNativeBox self, string name)
	{
		XAttribute? attribute = Require(self).Element.Attribute(name);
		return attribute?.Value;
	}

	/// <summary><c>child(name)</c>: первый дочерний элемент с именем или <c>nil</c>.</summary>
	[KernMember]
	public static KernXmlNode? Child(KNativeBox self, string name)
	{
		XElement? child = Require(self).Element.Elements().FirstOrDefault(
			element => element.Name.LocalName == name);
		return child is null ? null : new KernXmlNode(child);
	}

	/// <summary><c>find_all(name)</c>: все потомки с именем (в порядке документа).</summary>
	[KernName("find_all")]
	[KernMember]
	public static KList<KernXmlNode> FindAll(KNativeBox self, string name) =>
		Nodes(Require(self).Element.Descendants().Where(element => element.Name.LocalName == name));

	/// <summary><c>add(child)</c>: добавить дочерний элемент.</summary>
	[KernMember]
	public static void Add(KNativeBox self, KernXmlNode child) =>
		Require(self).Element.Add(new XElement(child.Element));

	/// <summary><c>to_str()</c>: сериализованный XML элемента.</summary>
	[KernMember]
	public static string ToStr(KNativeBox self) => Require(self).Element.ToString(SaveOptions.DisableFormatting);

	internal static KernXmlNode Require(KNativeBox self) => self.Value switch
	{
		KernXmlNode node => node,
		_ => throw new KernPanic($"ожидался xml_node, получено {self.Value.GetType().Name}"),
	};

	internal static KList<KernXmlNode> Nodes(IEnumerable<XElement> elements) =>
		new(KValue.Obj(new Kern.Vm.Values.KList(
			null,
			[.. elements.Select(element => KValue.Obj(new KNativeBox(new KernXmlNode(element))))])));
}

/// <summary>Модуль <c>kern.xml</c>: <c>parse/new</c>.</summary>
[KernModule("kern.xml")]
public static class XmlModule
{
	/// <summary><c>xml.parse(text)</c>: корневой элемент или <c>Err</c> с русским текстом.</summary>
	[KernMember]
	public static KResult<KernXmlNode, string> Parse(string text)
	{
		try
		{
			XDocument document = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
			return document.Root is null
				? KResult<KernXmlNode, string>.Failure("xml: в документе нет корневого элемента")
				: KResult<KernXmlNode, string>.Success(new KernXmlNode(document.Root));
		}
		catch (XmlException exception)
		{
			return KResult<KernXmlNode, string>.Failure($"xml: {exception.Message}");
		}
	}

	/// <summary><c>xml.new(name)</c>: новый пустой элемент.</summary>
	[KernName("new")]
	[KernMember]
	public static KernXmlNode New(string name) => new(new XElement(name));
}
