using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using RimWorldCodeRag.Common;

namespace RimWorldCodeRag.Indexer;

//xml的引用关系其实是我用硬代码一个个写的，不保证包含所有关系。如果有一种更优雅的解法就好了

//从 XML Def 中提取图谱边
public sealed class XmlGraphExtractor
{
    //提取 XML → C# 边（XmlBindsClass, XmlUsesComp）
    public IEnumerable<GraphEdge> ExtractXmlToCSharpEdges(XElement defElement, string defId, string? defType)
    {
        foreach (var edge in ExtractBindClassEdges(defElement, defId))
        {
            yield return edge;
        }

        foreach (var edge in ExtractUsesCompEdges(defElement, defId))
        {
            yield return edge;
        }
    }

    //XML -> XML 边（XmlInherits, XmlReferences）
    public IEnumerable<GraphEdge> ExtractXmlToXmlEdges(
        XElement defElement,
        string defId,
        string? defType,
        ICollection<string> validDefIds,
        ICollection<string> validDefNames,
        IReadOnlyDictionary<string, string[]> xmlSymbolToItemIds)
    {
        foreach (var edge in ExtractInheritsEdges(defElement, defId, xmlSymbolToItemIds))
        {
            yield return edge;
        }

        foreach (var edge in ExtractReferencesEdges(defElement, defId, defType, xmlSymbolToItemIds))
        {
            yield return edge;
        }
    }

    #region XML → C# Edge Extraction

    //类绑定边（thingClass, workerClass, verbClass, graphicClass等）
    private IEnumerable<GraphEdge> ExtractBindClassEdges(XElement defElement, string defId)
    {
        var classFieldCandidates = new[]
        {
            "thingClass",
            "workerClass",
            "driverClass",
            "hediffClass",
            "class",
            "aiController",
            "roomContentsWorkerType"
        };

        foreach (var fieldName in classFieldCandidates)
        {
            var className = defElement.Element(fieldName)?.Value;
            if (!string.IsNullOrWhiteSpace(className))
            {
                yield return new GraphEdge
                {
                    SourceId = defId,
                    TargetId = NormalizeClassName(className),
                    Kind = EdgeKind.XmlBindsClass
                };
            }
        }

        //嵌套类的title, 如graphicData/graphicClass, verbs/li/verbClass
        var graphicClass = defElement.Descendants("graphicData")
            .Elements("graphicClass")
            .FirstOrDefault()?.Value;
        if (!string.IsNullOrWhiteSpace(graphicClass))
        {
            yield return new GraphEdge
            {
                SourceId = defId,
                TargetId = NormalizeClassName(graphicClass),
                Kind = EdgeKind.XmlBindsClass
            };
        }

        var verbClasses = defElement.Descendants("verbs")
            .Elements("li")
            .Elements("verbClass")
            .Select(e => e.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v));

        foreach (var verbClass in verbClasses)
        {
            yield return new GraphEdge
            {
                SourceId = defId,
                TargetId = NormalizeClassName(verbClass),
                Kind = EdgeKind.XmlBindsClass
            };
        }
    }

    //comps 列表中的 CompProperties_*）
    private IEnumerable<GraphEdge> ExtractUsesCompEdges(XElement defElement, string defId)
    {
        var compElements = defElement.Descendants("comps")
            .Elements("li")
            .Select(li => li.Attribute("Class")?.Value)
            .Where(c => !string.IsNullOrWhiteSpace(c));

        foreach (var compClass in compElements)
        {
            yield return new GraphEdge
            {
                SourceId = defId,
                TargetId = NormalizeClassName(compClass!),
                Kind = EdgeKind.XmlUsesComp
            };
        }
    }

    //规范化类名确保包含命名空间
    private string NormalizeClassName(string className)
    {
        //其实是乱写的，主打一个猜
        if (className.Contains('.'))
            return className;

        if (className.StartsWith("CompProperties_") || className.StartsWith("Comp_"))
            return $"RimWorld.{className}";

        if (className.StartsWith("Verb_"))
            return $"Verse.{className}";

        if (className.StartsWith("Graphic_"))
            return $"Verse.{className}";

        if (className.StartsWith("Building_") || className.StartsWith("Thing_"))
            return $"RimWorld.{className}";

        return $"RimWorld.{className}";
    }

    #endregion

    #region XML → XML Edge Extraction

    private IEnumerable<GraphEdge> ExtractInheritsEdges(XElement defElement, string defId, IReadOnlyDictionary<string, string[]> xmlSymbolToItemIds)
    {
        var parentName = defElement.Attribute("ParentName")?.Value;

        if (string.IsNullOrWhiteSpace(parentName))
        {
            parentName = defElement.Element("ParentName")?.Value;
        }

        if (string.IsNullOrWhiteSpace(parentName))
        {
            yield break;
        }

        var defType = defElement.Name.LocalName;
        var symbolId = $"xml:{defType}:{parentName}";
        foreach (var edge in ExpandXmlTargets(defId, new[] { symbolId }, xmlSymbolToItemIds, EdgeKind.XmlInherits))
        {
            yield return edge;
        }
    }

    //提取引用边（其实不完整，但是我想不出其他来了）
    private IEnumerable<GraphEdge> ExtractReferencesEdges(XElement defElement, string defId, string? defType, IReadOnlyDictionary<string, string[]> xmlSymbolToItemIds)
    {
        return defType switch
        {
            "RecipeDef" => ExtractRecipeDefReferences(defElement, defId, xmlSymbolToItemIds),
            "PawnKindDef" => ExtractPawnKindDefReferences(defElement, defId, xmlSymbolToItemIds),
            "ResearchProjectDef" => ExtractResearchProjectReferences(defElement, defId, xmlSymbolToItemIds),
            "ThingDef" => ExtractThingDefReferences(defElement, defId, xmlSymbolToItemIds),
            _ => Enumerable.Empty<GraphEdge>()
        };
    }

    private static IEnumerable<GraphEdge> ExpandXmlTargets(string sourceId, IEnumerable<string> symbolIds, IReadOnlyDictionary<string, string[]> xmlSymbolToItemIds, EdgeKind kind)
    {
        foreach (var symbolId in symbolIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!xmlSymbolToItemIds.TryGetValue(symbolId, out var targetIds))
            {
                continue;
            }

            foreach (var targetId in targetIds)
            {
                yield return new GraphEdge
                {
                    SourceId = sourceId,
                    TargetId = targetId,
                    Kind = kind
                };
            }
        }
    }

    private IEnumerable<GraphEdge> ExtractRecipeDefReferences(XElement element, string defId, IReadOnlyDictionary<string, string[]> xmlSymbolToItemIds)
    {
        var referencedSymbolIds = element.Descendants("products")
            .Elements()
            .Select(e => $"xml:ThingDef:{e.Name.LocalName}")
            .Concat(element.Descendants("ingredients")
                .SelectMany(ing => ing.Descendants("thingDefs").Elements())
                .Select(e => $"xml:ThingDef:{e.Value}"));

        foreach (var edge in ExpandXmlTargets(defId, referencedSymbolIds, xmlSymbolToItemIds, EdgeKind.XmlReferences))
        {
            yield return edge;
        }
    }

    private IEnumerable<GraphEdge> ExtractPawnKindDefReferences(XElement element, string defId, IReadOnlyDictionary<string, string[]> xmlSymbolToItemIds)
    {
        var race = element.Element("race")?.Value;
        if (string.IsNullOrWhiteSpace(race))
        {
            yield break;
        }

        foreach (var edge in ExpandXmlTargets(defId, new[] { $"xml:ThingDef:{race}" }, xmlSymbolToItemIds, EdgeKind.XmlReferences))
        {
            yield return edge;
        }
    }

    private IEnumerable<GraphEdge> ExtractResearchProjectReferences(XElement element, string defId, IReadOnlyDictionary<string, string[]> xmlSymbolToItemIds)
    {
        var referencedSymbolIds = element.Descendants("prerequisites")
            .Elements("li")
            .Select(e => $"xml:ResearchProjectDef:{e.Value}");

        foreach (var edge in ExpandXmlTargets(defId, referencedSymbolIds, xmlSymbolToItemIds, EdgeKind.XmlReferences))
        {
            yield return edge;
        }
    }

    private IEnumerable<GraphEdge> ExtractThingDefReferences(XElement element, string defId, IReadOnlyDictionary<string, string[]> xmlSymbolToItemIds)
    {
        var referencedSymbolIds = element.Descendants("costList")
            .Elements()
            .Select(e => $"xml:ThingDef:{e.Name.LocalName}");

        foreach (var edge in ExpandXmlTargets(defId, referencedSymbolIds, xmlSymbolToItemIds, EdgeKind.XmlReferences))
        {
            yield return edge;
        }
    }

    #endregion
}
