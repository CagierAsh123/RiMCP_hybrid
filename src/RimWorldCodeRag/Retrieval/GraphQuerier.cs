using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using F23.StringSimilarity;
using RimWorldCodeRag.Common;

namespace RimWorldCodeRag.Retrieval;

public sealed class GraphQuerier : IDisposable
{
    private const int PageSize = 30;
    private const double PageRankScaleFactor = 1e7;
    private readonly JaroWinkler _jaroWinkler = new JaroWinkler();

    private readonly string _basePath;
    private readonly Dictionary<int, GraphNodeRecord> _indexToNode;
    private readonly Dictionary<string, int> _itemIdToIndex;
    private readonly Dictionary<string, List<int>> _symbolToIndexes;
    
    //行优先的压缩稀疏矩阵图
    private readonly int[] _csrRowPointers;
    private readonly int[] _csrColumnIndices;
    private readonly byte[] _csrKinds;

    //列优先的压缩稀疏矩阵图
    private readonly int[] _cscColPointers;
    private readonly int[] _cscRowIndices;
    private readonly byte[] _cscKinds;
    
    private readonly int _nodeCount;
    private readonly int _edgeCount;

    private readonly Dictionary<string, double> _pageRankScores;
    private readonly Dictionary<EdgeKind, double> _edgeWeights;

    public GraphQuerier(string basePath)
    {
        _basePath = basePath;
        
        (_indexToNode, _itemIdToIndex, _symbolToIndexes) = LoadNodes(basePath + ".nodes.tsv");
        _nodeCount = _indexToNode.Count;
        
        (_csrRowPointers, _csrColumnIndices, _csrKinds) = LoadBinary(basePath + ".csr.bin", "CSR1");
        
        (_cscColPointers, _cscRowIndices, _cscKinds) = LoadBinary(basePath + ".csc.bin", "CSC1");
        
        _edgeCount = _csrColumnIndices.Length;

        _pageRankScores = LoadPageRank(basePath + ".pagerank.tsv");
        _edgeWeights = new Dictionary<EdgeKind, double>
        {
            { EdgeKind.Inherits, 2.0 },
            { EdgeKind.XmlInherits, 1.8 },
            { EdgeKind.Implements, 0.9 },
            { EdgeKind.Calls, 0.8 },
            { EdgeKind.XmlBindsClass, 0.7 },
            { EdgeKind.XmlUsesComp, 0.6 },
            { EdgeKind.References, 0.5 },
            { EdgeKind.XmlReferences, 0.4 },
            { EdgeKind.CSharpUsedByDef, 0.7 }
        };
    }

    //执行检索
    public PagedGraphQueryResult Query(GraphQueryConfig config)
    {
        var startIndexes = ResolveStartIndexes(config);
        if (startIndexes.Count == 0)
        {
            return new PagedGraphQueryResult
            {
                Results = Array.Empty<GraphQueryResult>(),
                TotalCount = 0,
                Page = config.Page,
                PageSize = PageSize
            };
        }

        var allResults = new List<GraphQueryResult>();
        foreach (var nodeIndex in startIndexes)
        {
            var originNode = _indexToNode[nodeIndex];
            var edges = config.Direction == GraphDirection.Uses
                ? GetOutgoingEdges(nodeIndex)
                : GetIncomingEdges(nodeIndex);

            edges = edges.Where(e => IsEdgeValidForDirection(DecodeKind(e.Kind), config.Direction));
            if (!string.IsNullOrWhiteSpace(config.Kind))
            {
                edges = FilterByKind(edges, config.Kind, config.Direction);
            }

            var nodeResults = edges
                .GroupBy(e => new
                {
                    Node = config.Direction == GraphDirection.Uses ? _indexToNode[e.TargetIndex] : _indexToNode[e.SourceIndex],
                    EdgeKind = DecodeKind(e.Kind)
                })
                .Select(g =>
                {
                    var node = g.Key.Node;
                    var edgeKind = g.Key.EdgeKind;
                    var duplicateCount = g.Count();

                    var rawPageRank = _pageRankScores.TryGetValue(node.ItemId, out var pr)
                        ? pr
                        : _pageRankScores.TryGetValue(node.SymbolId, out pr)
                            ? pr
                            : 0.0;
                    var scaledPageRank = rawPageRank * PageRankScaleFactor;

                    var edgeWeight = _edgeWeights.TryGetValue(edgeKind, out var ew) ? ew : 0.1;
                    var lexicalBonus = _jaroWinkler.Similarity(originNode.SymbolId, node.SymbolId);

                    var initialScore = scaledPageRank * edgeWeight;
                    var finalScore = (initialScore * Math.Sqrt(duplicateCount)) * lexicalBonus;

                    return new GraphQueryResult
                    {
                        ItemId = node.ItemId,
                        SymbolId = node.SymbolId,
                        EdgeKind = edgeKind,
                        Distance = 1,
                        Score = finalScore,
                        PageRank = scaledPageRank,
                        DuplicateCount = duplicateCount
                    };
                });

            allResults.AddRange(nodeResults);
        }

        var mergedResults = allResults
            .GroupBy(r => new { r.ItemId, r.EdgeKind })
            .Select(g => g.OrderByDescending(r => r.Score).First())
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.SymbolId, StringComparer.Ordinal)
            .ThenBy(r => r.ItemId, StringComparer.Ordinal)
            .ToList();

        var pagedResults = mergedResults
            .Skip((config.Page - 1) * PageSize)
            .Take(PageSize)
            .ToList();

        return new PagedGraphQueryResult
        {
            Results = pagedResults,
            TotalCount = mergedResults.Count,
            Page = config.Page,
            PageSize = PageSize
        };
    }

    //出边。不是外向型人格
    private IEnumerable<RawEdge> GetOutgoingEdges(int nodeIndex)
    {
        var start = _csrRowPointers[nodeIndex];
        var end = _csrRowPointers[nodeIndex + 1];
        
        for (var i = start; i < end; i++)
        {
            yield return new RawEdge
            {
                SourceIndex = nodeIndex,
                TargetIndex = _csrColumnIndices[i],
                Kind = _csrKinds[i]
            };
        }
    }

    //入边。不是收入
    private IEnumerable<RawEdge> GetIncomingEdges(int nodeIndex)
    {
        var start = _cscColPointers[nodeIndex];
        var end = _cscColPointers[nodeIndex + 1];
        
        for (var i = start; i < end; i++)
        {
            yield return new RawEdge
            {
                SourceIndex = _cscRowIndices[i],
                TargetIndex = nodeIndex,
                Kind = _cscKinds[i]
            };
        }
    }

// c# 或 xml的筛选
    private IEnumerable<RawEdge> FilterByKind(IEnumerable<RawEdge> edges, string kind, GraphDirection direction)
    {
        var kindLower = kind.ToLowerInvariant();
        var wantCSharp = kindLower == "csharp" || kindLower == "cs";
        var wantXml = kindLower == "xml" || kindLower == "def";

        if (!wantCSharp && !wantXml)
        {
            return edges;
        }

        return edges.Where(e =>
        {
            var resultNode = direction == GraphDirection.Uses
                ? _indexToNode[e.TargetIndex]
                : _indexToNode[e.SourceIndex];

            var resultIsCSharp = IsCSharpNode(resultNode.SymbolId);
            var resultIsXml = IsXmlNode(resultNode.SymbolId);
            
            if (wantCSharp)
            {
                return resultIsCSharp;
            }
            else 
            {
                return resultIsXml;
            }
        });
    }

    private static bool IsCSharpNode(string symbolId)
        => !symbolId.StartsWith("xml:", StringComparison.OrdinalIgnoreCase);

    private static bool IsXmlNode(string symbolId)
        => symbolId.StartsWith("xml:", StringComparison.OrdinalIgnoreCase);

    private static bool IsCSharpEdge(EdgeKind kind) => kind switch
    {
        EdgeKind.Inherits => true,
        EdgeKind.Calls => true,
        EdgeKind.References => true,
        EdgeKind.XmlBindsClass => true,
        EdgeKind.XmlUsesComp => true,
        _ => false
    };

    private static bool IsXmlEdge(EdgeKind kind) => kind switch
    {
        EdgeKind.XmlInherits => true,
        EdgeKind.XmlReferences => true,
        EdgeKind.CSharpUsedByDef => true,
        _ => false
    };

    //多查一次：CSharpUsedByDef是反向边不能出现在Uses查询中。测试的时候发现这个问题，多做一步
    private static bool IsEdgeValidForDirection(EdgeKind kind, GraphDirection direction)
    {
        if (direction == GraphDirection.Uses)
        {
            return kind != EdgeKind.CSharpUsedByDef;
        }
        return true;
    }

    private static EdgeKind DecodeKind(byte code) => code switch
    {
        1 => EdgeKind.Calls,
        2 => EdgeKind.References,
        3 => EdgeKind.Inherits,
        4 => EdgeKind.XmlReferences,
        10 => EdgeKind.XmlInherits,
        20 => EdgeKind.XmlBindsClass,
        21 => EdgeKind.XmlUsesComp,
        30 => EdgeKind.CSharpUsedByDef,
        _ => EdgeKind.References 
    };

    private IReadOnlyList<int> ResolveStartIndexes(GraphQueryConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.ItemId) && _itemIdToIndex.TryGetValue(config.ItemId, out var itemIndex))
        {
            return new[] { itemIndex };
        }

        if (!string.IsNullOrWhiteSpace(config.SymbolId) && _symbolToIndexes.TryGetValue(config.SymbolId, out var symbolIndexes))
        {
            return symbolIndexes;
        }

        return Array.Empty<int>();
    }

    private static (Dictionary<int, GraphNodeRecord>, Dictionary<string, int>, Dictionary<string, List<int>>) LoadNodes(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Graph nodes file not found: {path}");
        }

        var indexToNode = new Dictionary<int, GraphNodeRecord>();
        var itemIdToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var symbolToIndexes = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            var parts = line.Split('\t');
            if (parts.Length < 2)
            {
                continue;
            }

            if (!int.TryParse(parts[0], out var index))
            {
                continue;
            }

            var itemId = parts[1];
            var symbolId = parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[2]) ? parts[2] : itemId;
            var node = new GraphNodeRecord(itemId, symbolId);

            indexToNode[index] = node;
            itemIdToIndex[itemId] = index;

            if (!symbolToIndexes.TryGetValue(symbolId, out var indexes))
            {
                indexes = new List<int>();
                symbolToIndexes[symbolId] = indexes;
            }

            indexes.Add(index);
        }

        return (indexToNode, itemIdToIndex, symbolToIndexes);
    }

    private static Dictionary<string, double> LoadPageRank(string path)
    {
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return scores;
        }

        using var reader = new StreamReader(path, Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var parts = line.Split('\t');
            if (parts.Length != 2 || !double.TryParse(parts[1], out var score))
            {
                continue;
            }

            scores[parts[0]] = score;
        }

        return scores;
    }

    public static (int[], int[], byte[]) LoadBinary(string path, string expectedHeader)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

        //读标题
        var magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (magic != expectedHeader)
        {
            throw new InvalidDataException($"Invalid magic header in {path}: expected {expectedHeader}, got {magic}");
        }

        var version = reader.ReadInt32();
        if (version != 1)
        {
            throw new InvalidDataException($"Unsupported format version: {version}");
        }

        var nodeCount = reader.ReadInt32();
        var edgeCount = reader.ReadInt32();

        //读指针
        var pointers = new int[nodeCount + 1];
        for (var i = 0; i <= nodeCount; i++)
        {
            pointers[i] = reader.ReadInt32();
        }

        //读索引
        var indices = new int[edgeCount];
        for (var i = 0; i < edgeCount; i++)
        {
            indices[i] = reader.ReadInt32();
        }

        //读类
        var kindsLength = reader.ReadInt32();
        if (kindsLength != edgeCount)
        {
            throw new InvalidDataException($"Kinds array length mismatch: expected {edgeCount}, got {kindsLength}");
        }
        var kinds = reader.ReadBytes(edgeCount);

        return (pointers, indices, kinds);
    }

    public void Dispose()
    {
        //好像没啥需要释放的，索性全删了
    }

    private readonly struct GraphNodeRecord
    {
        public GraphNodeRecord(string itemId, string symbolId)
        {
            ItemId = itemId;
            SymbolId = symbolId;
        }

        public string ItemId { get; }
        public string SymbolId { get; }
    }

    private readonly struct RawEdge
    {
        public int SourceIndex { get; init; }
        public int TargetIndex { get; init; }
        public byte Kind { get; init; }
    }
}
