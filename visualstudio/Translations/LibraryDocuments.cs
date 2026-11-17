using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.LanguageServer.Client;
using Newtonsoft.Json.Linq;
using RunicArtifex.DevKit.VisualStudio.Core;

namespace RunicArtifex.DevKit.VisualStudio.Translations;

/// <summary>
/// Read-only library documents. Go to Definition in an override source answers with the library's source as a
/// <c>runic-library:</c> URI, which VS Code opens through a content provider. Visual Studio's LSP client opens only files,
/// so this middle layer of <see cref="RunicLanguageClient"/> reads each such location's text with
/// <c>runic/libraryDocument</c>, writes it to a read-only file under the extension's temporary directory
/// (<see cref="LibraryCatalogs.DocumentPath"/>) and answers with that file instead. The ranges stay valid because the
/// text is the same.
/// </summary>
internal sealed class LibraryDocuments : ILanguageClientMiddleLayer2<JToken>
{
    private static readonly HashSet<string> LocationMethods = new(StringComparer.Ordinal)
    {
        "textDocument/definition", "textDocument/declaration", "textDocument/typeDefinition", "textDocument/implementation", "textDocument/references",
    };

    private readonly Func<string, object, CancellationToken, Task<JObject>> request;

    public LibraryDocuments(Func<string, object, CancellationToken, Task<JObject>> request) { this.request = request; }

    public bool CanHandle(string methodName) => LocationMethods.Contains(methodName);

    public Task HandleNotificationAsync(string methodName, JToken methodParam, Func<JToken, Task> sendNotification) => sendNotification(methodParam);

    public async Task<JToken?> HandleRequestAsync(string methodName, JToken methodParam, Func<JToken, Task<JToken?>> sendRequest)
    {
        var result = await sendRequest(methodParam);
        if (result == null) return null;
        var tokens = result is JContainer container ? container.DescendantsAndSelf() : new[] { result };
        var locations = tokens.OfType<JObject>()
            .SelectMany(item => new[] { "uri", "targetUri" }.Select(key => (Item: item, Key: key, Uri: item.Value<string>(key))))
            .Where(entry => LibraryCatalogs.IsLibraryUri(entry.Uri)).ToList();
        foreach (var location in locations)
        {
            try { location.Item[location.Key] = await MaterializeAsync(location.Uri!); }
            catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
            {
                System.Diagnostics.Trace.WriteLine($"Runic: library document {location.Uri}: {error.Message}");
            }
        }
        return result;
    }

    /// <summary>The file URI of the read-only copy of a library source.</summary>
    private async Task<string> MaterializeAsync(string uri)
    {
        string path = LibraryCatalogs.DocumentPath(TranslationsWorkspace.TemporaryDirectory("library"), uri)
            ?? throw new InvalidOperationException("Not a library document URI.");
        var document = await request("runic/libraryDocument", new { uri }, CancellationToken.None);
        TranslationsWorkspace.WriteReadOnly(path, document.Value<string>("text") ?? "");
        return new Uri(path).AbsoluteUri;
    }
}
