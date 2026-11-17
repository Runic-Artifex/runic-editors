using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace RunicArtifex.DevKit.VisualStudio.Core;

/// <summary>A 1-based source position of the development source map.</summary>
public sealed class SourceLocation
{
    public SourceLocation(string file, int line, int column) { File = file; Line = line; Column = column; }

    public string File { get; }
    public int Line { get; }
    public int Column { get; }

    public override string ToString() => $"{File}:{Line}:{Column}";
}

public sealed class SourceViewModel
{
    internal SourceViewModel(string id, string name, string? route, string? symbol, SourceLocation? location)
    {
        Id = id; Name = name; Route = route; Symbol = symbol; Location = location;
    }

    public string Id { get; }
    public string Name { get; }
    public string? Route { get; }
    public string? Symbol { get; }
    public SourceLocation? Location { get; }
}

public sealed class SourceMember
{
    internal SourceMember(string id, string viewModel, string member, string kind, string? symbol, string? property, SourceLocation? location)
    {
        Id = id; ViewModel = viewModel; Member = member; Kind = kind; Symbol = symbol; Property = property; Location = location;
    }

    public string Id { get; }
    public string ViewModel { get; }
    public string Member { get; }
    /// <summary><c>state</c>, <c>setter</c>, <c>command</c>, <c>commandState</c>, <c>interaction</c>, <c>request</c>, or a newer kind.</summary>
    public string Kind { get; }
    public string? Symbol { get; }
    public string? Property { get; }
    public SourceLocation? Location { get; }
}

/// <summary>
/// The development source map that the Bridge generator writes to
/// <c>obj/&lt;Configuration&gt;/&lt;TargetFramework&gt;/runic-bridge/sources.json</c> (Runic.Application.Views README,
/// "Development source map"), read as VS Code's <c>vscode/src/source-map.ts</c> reads it.
/// </summary>
public sealed class SourceMap
{
    /// <summary>The accepted format IDs: runic-sdk 0.21's, then the 0.20 one.</summary>
    public static IReadOnlyList<string> Formats { get; } = new[] { "runic.views.bridge-sources/1", "runic-bridge-sources/1" };

    private SourceMap(string format, IReadOnlyList<SourceViewModel> viewModels, IReadOnlyList<SourceMember> members)
    {
        Format = format; ViewModels = viewModels; Members = members;
    }

    public string Format { get; }
    public IReadOnlyList<SourceViewModel> ViewModels { get; }
    public IReadOnlyList<SourceMember> Members { get; }

    public static SourceMap Parse(string text)
    {
        var map = Json.Object(Json.Parse(text)) ?? throw new FormatException("The development source map is not a JSON object.");
        string? format = Json.String(map, "format");
        if (format == null || !Formats.Contains(format))
            throw new FormatException($"Unsupported development source map format {(format == null ? "null" : "\"" + format + "\"")}; this extension reads {string.Join(" and ", Formats)}.");
        var viewModels = Json.Array(Json.Get(map, "viewModels"));
        var members = Json.Array(Json.Get(map, "members"));
        if (viewModels == null || members == null) throw new FormatException("The development source map has no viewModels or members.");
        return new SourceMap(format,
            viewModels.Select(Json.Object).Where(entry => entry != null && Json.String(entry, "id") != null).Select(entry => new SourceViewModel(
                Json.String(entry, "id")!, Json.String(entry, "name") ?? "", Json.String(entry, "route"), Json.String(entry, "symbol"), Location(entry))).ToList(),
            members.Select(Json.Object).Where(entry => entry != null && Json.String(entry, "id") != null && Json.String(entry, "viewModel") != null).Select(entry => new SourceMember(
                Json.String(entry, "id")!, Json.String(entry, "viewModel")!, Json.String(entry, "member") ?? Json.String(entry, "id")!, Json.String(entry, "kind") ?? "",
                Json.String(entry, "symbol"), Json.String(entry, "property"), Location(entry))).ToList());
    }

    private static SourceLocation? Location(Dictionary<string, object?>? entry)
    {
        var location = Json.Object(Json.Get(entry, "location"));
        string? file = Json.String(location, "file");
        if (string.IsNullOrEmpty(file)) return null;
        return new SourceLocation(file!, (int)(Json.Number(location, "line") ?? 1), (int)(Json.Number(location, "column") ?? 1));
    }

    /// <summary>
    /// The newest <c>runic-bridge/sources.json</c> under the project's <c>obj</c>
    /// (<c>obj/&lt;Configuration&gt;/&lt;TargetFramework&gt;/</c>), as the Vite plugin picks it; null before the first build.
    /// </summary>
    public static string? Newest(string project)
    {
        string obj = Path.Combine(Path.GetDirectoryName(project) ?? "", "obj");
        string? newest = null;
        var newestTime = DateTime.MinValue;
        foreach (string configuration in Directories(obj))
            foreach (string framework in Directories(configuration))
            {
                string candidate = Path.Combine(framework, "runic-bridge", "sources.json");
                if (!File.Exists(candidate)) continue;
                var time = File.GetLastWriteTimeUtc(candidate);
                if (newest == null || time > newestTime) { newest = candidate; newestTime = time; }
            }
        return newest;
    }

    private static IEnumerable<string> Directories(string path)
    {
        try { return Directory.GetDirectories(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }
}

/// <summary>One node of the ViewModels tree: a ViewModel with its members, or a member.</summary>
public sealed class SourceTreeNode
{
    internal SourceTreeNode(string id, string label, string kind, string? description, string tooltip, SourceLocation? location)
    {
        Id = id; Label = label; Kind = kind; Description = description; Tooltip = tooltip; Location = location;
    }

    public string Id { get; }
    public string Label { get; }
    /// <summary><see cref="SourceMapTree.ViewModelKind"/> or the member's kind.</summary>
    public string Kind { get; }
    public string? Description { get; }
    public string Tooltip { get; }
    public SourceLocation? Location { get; }
    public List<SourceTreeNode> Children { get; } = new();
}

public static class SourceMapTree
{
    public const string ViewModelKind = "viewModel";
    public const string NoLocation = "no source location (build with a PDB)";

    private static readonly Dictionary<string, int> KindOrder = new() { ["state"] = 0, ["setter"] = 1, ["commandState"] = 2, ["command"] = 3, ["interaction"] = 4, ["request"] = 5 };
    private static readonly Dictionary<string, string> KindLabel = new() { ["state"] = "state", ["setter"] = "setter", ["commandState"] = "command state", ["command"] = "command", ["interaction"] = "interaction", ["request"] = "View request" };

    /// <summary>
    /// The ViewModels tree: every ViewModel (by name, with its route) and below it its members in client order (state,
    /// setters, command state, commands, interactions, View requests), each with its C# location. Members whose
    /// ViewModel the map does not list are kept under a ViewModel node of their own.
    /// </summary>
    public static IReadOnlyList<SourceTreeNode> Build(SourceMap map)
    {
        var nodes = new Dictionary<string, SourceTreeNode>(StringComparer.Ordinal);
        var order = new List<SourceTreeNode>();
        foreach (var viewModel in map.ViewModels)
        {
            if (nodes.ContainsKey(viewModel.Id)) continue;
            var node = new SourceTreeNode(viewModel.Id, viewModel.Name.Length > 0 ? viewModel.Name : viewModel.Id, ViewModelKind,
                viewModel.Route != null ? "route " + viewModel.Route : null,
                string.Join("\n", viewModel.Symbol ?? viewModel.Id, viewModel.Location?.ToString() ?? NoLocation), viewModel.Location);
            order.Add(node);
            nodes[viewModel.Id] = node;
        }
        foreach (var member in map.Members)
        {
            if (!nodes.TryGetValue(member.ViewModel, out var parent))
            {
                parent = new SourceTreeNode(member.ViewModel, member.ViewModel, ViewModelKind, null, member.ViewModel, null);
                nodes[member.ViewModel] = parent;
                order.Add(parent);
            }
            var lines = new List<string> { member.Symbol ?? member.Id };
            if (member.Property != null && member.Property != member.Symbol) lines.Add("via " + member.Property);
            lines.Add(member.Location?.ToString() ?? NoLocation);
            parent.Children.Add(new SourceTreeNode(member.Id, member.Member, member.Kind,
                KindLabel.TryGetValue(member.Kind, out string? label) ? label : member.Kind, string.Join("\n", lines), member.Location));
        }
        var comparer = StringComparer.Create(CultureInfo.InvariantCulture, ignoreCase: true);
        foreach (var node in order)
            node.Children.Sort((a, b) => Rank(a.Kind).CompareTo(Rank(b.Kind)) is var byKind && byKind != 0 ? byKind : comparer.Compare(a.Label, b.Label));
        return order.OrderBy(node => node.Label, comparer).ToList();
    }

    private static int Rank(string kind) => KindOrder.TryGetValue(kind, out int rank) ? rank : 9;
}
