// VsixPublisher's `publish` checks, offline: the steps of PublishCommand.Execute and Publisher.PublishCoreAsync of
// Microsoft.VSSDK.BuildTools 18.11.1098 (eng/release/vsixpublisher) up to its first Marketplace request, called by
// reflection in the same order and with VsixPublisher's own logger, so a failure prints VsixPublisher's message and
// error code ("VSSDK: error VsixPub0024 : ...") and the exit code is that code. The real `publish` authenticates
// before these steps; nothing here authenticates or opens a connection.
//   VsixPublisherCheck <VsixPublisher.exe> <payload.vsix> <publishManifest.json> [<upload package output>]
// It first checks the binding redirects VsixPublisher's authentication needs (VsixPub0031 in 18.12.1606).
using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using System.Xml.Linq;

if (args.Length is < 3 or > 4)
{
    Console.Error.WriteLine("Usage: VsixPublisherCheck <VsixPublisher.exe> <payload.vsix> <publishManifest.json> [<upload package output>]");
    return 2;
}
var exePath = Path.GetFullPath(args[0]);
var payload = Path.GetFullPath(args[1]);
var publishManifestPath = Path.GetFullPath(args[2]);
var outputPackage = args.Length == 4 ? Path.GetFullPath(args[3]) : null;
var bin = Path.GetDirectoryName(exePath)!;
if (!File.Exists(exePath))
{
    Console.Error.WriteLine($"{exePath} does not exist; restore eng/release/vsixpublisher first (dotnet restore eng/release/vsixpublisher --locked-mode).");
    return 2;
}

// VsixPublisher.exe.config: <probing privatePath="lib\vsixpublisher;lib" /> and the binding redirects.
XNamespace asm = "urn:schemas-microsoft-com:asm.v1";
var config = XDocument.Load(exePath + ".config");
var probeDirectories = new[] { bin }.Concat(((string?)config.Descendants(asm + "probing").FirstOrDefault()?.Attribute("privatePath") ?? "")
    .Split(';', StringSplitOptions.RemoveEmptyEntries).Select(path => Path.Combine(bin, path.Replace('\\', Path.DirectorySeparatorChar))))
    .Where(Directory.Exists).ToArray();

var redirectProblems = BindingRedirects.Problems(exePath, probeDirectories, config, asm);
if (redirectProblems.Count > 0)
{
    // .NET Framework refuses these loads at run time, which VsixPublisher reports as VsixPub0031 when it authenticates.
    foreach (var problem in redirectProblems) Console.Error.WriteLine($"VsixPublisherCheck: error VsixPub0031 : {problem}");
    return 31;
}

AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    foreach (var directory in probeDirectories)
    {
        var path = Path.Combine(directory, name.Name + ".dll");
        if (File.Exists(path)) return context.LoadFromAssemblyPath(path);
    }
    return null;
};
var vsixPublisher = AssemblyLoadContext.Default.LoadFromAssemblyPath(exePath);
var commandLine = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(bin, "VsixCommandLine.dll"));
const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
Type Type(Assembly assembly, string name) => assembly.GetType(name, throwOnError: true)!;
object Create(Type type, params object?[] arguments) =>
    type.GetConstructors(Any).Single(constructor => constructor.GetParameters().Length == arguments.Length).Invoke(arguments);
object? Call(object target, string method, params object?[] arguments) =>
    target.GetType().GetMethod(method, Any) is { } info ? info.Invoke(target, arguments) : throw new MissingMethodException(target.GetType().FullName, method);
const string Sdk = "Microsoft.VisualStudio.Sdk.VsixPublisher";

var fileSystem = Type(commandLine, "Microsoft.VisualStudio.Sdk.FileSystem").GetField("Instance")!.GetValue(null)!;
var logger = Create(Type(commandLine, "Microsoft.VisualStudio.Sdk.Logger"), Console.OpenStandardError(), Console.OpenStandardOutput());
var extracted = new List<string>();
int Failed()
{
    var error = logger.GetType().GetProperty("LastError")!.GetValue(logger);
    ((IDisposable)logger).Dispose();
    foreach (var file in extracted) File.Delete(file);
    return error is null ? 1 : (int)error.GetType().GetProperty("ErrorCode")!.GetValue(error)!;
}
void LogError(string message, params object?[] arguments)
{
    var output = Type(vsixPublisher, $"{Sdk}.VsixPublisherOutputMessages").GetProperty(message, Any)!.GetValue(null);
    logger.GetType().GetMethod("LogError", [output!.GetType(), typeof(object[])])!.Invoke(logger, [output, arguments]);
}

// PublishCommand.Validate and Execute: the payload and the publish manifest exist, and the manifest deserializes.
var command = Create(Type(vsixPublisher, $"{Sdk}.Commands.PublishCommand"), fileSystem, logger);
var selected = new Dictionary<string, object> { ["payload"] = payload, ["publishManifest"] = publishManifestPath };
if ((int)Call(command, "Validate", selected)! != 0) return Failed();
object?[] read = [publishManifestPath, null];
if (!(bool)Call(command, "TryReadPublishManifest", read)!) return Failed();
var publishManifest = read[1]!;

// Publisher.PublishCoreAsync for a VSIX payload, up to marketplaceClient.GetExtensionAsync.
var publisher = Create(Type(vsixPublisher, $"{Sdk}.Publisher"), fileSystem, logger);
if (!(bool)Call(publisher, "TryAddIdentityForVsixPayload", payload, publishManifest)!) return Failed();
var identity = publishManifest.GetType().GetProperty("Identity")!.GetValue(publishManifest)!;
var iconProperty = identity.GetType().GetProperty("Icon")!;
if (iconProperty.GetValue(identity) is string { Length: > 0 } icon)
{
    var temporary = Path.GetTempFileName();
    extracted.Add(temporary);
    if (!(bool)Call(publisher, "TryExtractFileFromVsix", payload, icon, temporary)!)
    {
        LogError("Error_CannotFindFileInVsix", icon, temporary);
        return Failed();
    }
    iconProperty.SetValue(identity, temporary);
}
var basePath = Path.GetDirectoryName(publishManifestPath)!;
if (!(bool)Call(publisher, "ValidatePublishManifest", publishManifest, basePath)!) return Failed();
var assetFileType = Type(vsixPublisher, $"{Sdk}.DataContracts.AssetFile");
var assetFiles = (System.Collections.IList)publishManifest.GetType().GetProperty("AssetFiles")!.GetValue(publishManifest)!;
object Asset(string path, string target, string type)
{
    var asset = Activator.CreateInstance(assetFileType)!;
    assetFileType.GetProperty("PathOnDisk", Any)!.SetValue(asset, path);
    assetFileType.GetProperty("TargetPathInPackage", Any)!.SetValue(asset, target);
    assetFileType.GetProperty("AssetFileType", Any)!.SetValue(asset, type);
    return asset;
}
assetFiles.Add(Asset((string)publishManifest.GetType().GetProperty("OverviewFile")!.GetValue(publishManifest)!, "/overview.md",
    "Microsoft.VisualStudio.Services.Content.Details"));
if (iconProperty.GetValue(identity) is string { Length: > 0 } iconFile)
    assetFiles.Add(Asset(iconFile, "/" + Path.GetFileName(iconFile), "Microsoft.VisualStudio.Services.Icons.Default"));
var payloadType = Enum.Parse(Type(vsixPublisher, $"{Sdk}.PayloadType"), "Vsix");
var marketplaceManifest = (string)Call(Create(Type(vsixPublisher, $"{Sdk}.MarketplaceManifestGenerator")), "GenerateManifest", payload, payloadType, publishManifest)!;
var packager = Create(Type(vsixPublisher, $"{Sdk}.PayloadPackager"), fileSystem, logger, basePath);
object?[] package = [publishManifest, marketplaceManifest, null, payload];
if (!(bool)Call(packager, "TryGetPackageStream", package)!) return Failed();
using var packageStream = (Stream)package[2]!;
if (outputPackage is not null)
{
    using var output = File.Create(outputPackage);
    packageStream.Seek(0, SeekOrigin.Begin);
    packageStream.CopyTo(output);
}
var publisherName = publishManifest.GetType().GetProperty("Publisher")!.GetValue(publishManifest);
var internalName = identity.GetType().GetProperty("InternalName")!.GetValue(identity);
Console.WriteLine($"VsixPublisher {System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath).FileVersion} offline publish checks passed for {publisherName}.{internalName}: "
    + $"{Path.GetFileName(payload)} would upload as a {packageStream.Length}-byte package{(outputPackage is null ? "" : $" ({outputPackage})")}.");
((IDisposable)logger).Dispose();
foreach (var file in extracted) File.Delete(file);
return 0;

static class BindingRedirects
{
    // The assemblies on VsixPublisher's publish and authentication path. A reference from one of them to a
    // strong-named assembly that the package ships in another version needs a binding redirect to that version, or
    // .NET Framework refuses the load. 18.12.1606 lacks the Microsoft.Identity.Client redirect
    // (Microsoft.VisualStudio.Services.Common references 4.85.2.0, the package ships 4.87.0.0), and every publish fails
    // with VsixPub0031. Other bundled assemblies (SQL client, telemetry exporters) have unredirected references that
    // `publish` never loads, so they are not checked.
    static readonly Regex PublishPath = new(@"^(VsixPublisher|VsixCommandLine|Microsoft\.VisualStudio\.Services\..+|Microsoft\.Identity\.Client.*)$");

    public static List<string> Problems(string exePath, string[] directories, XDocument config, XNamespace asm)
    {
        var shipped = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);
        var references = new List<(string From, string Name, Version Version)>();
        foreach (var file in new[] { exePath }.Concat(directories.SelectMany(directory => Directory.GetFiles(directory, "*.dll"))))
        {
            using var stream = File.OpenRead(file);
            using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
            if (!pe.HasMetadata) continue;
            var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            if (!metadata.IsAssembly) continue;
            var definition = metadata.GetAssemblyDefinition();
            var name = metadata.GetString(definition.Name);
            // The loader takes the first match in probing order.
            if (!shipped.TryAdd(name, definition.Version)) continue;
            foreach (var handle in metadata.AssemblyReferences)
            {
                var reference = metadata.GetAssemblyReference(handle);
                if (!reference.PublicKeyOrToken.IsNil) references.Add((name, metadata.GetString(reference.Name), reference.Version));
            }
        }
        var redirects = config.Descendants(asm + "dependentAssembly").Select(element => (
            Name: (string?)element.Element(asm + "assemblyIdentity")?.Attribute("name"),
            Old: (string?)element.Element(asm + "bindingRedirect")?.Attribute("oldVersion"),
            New: (string?)element.Element(asm + "bindingRedirect")?.Attribute("newVersion"))).ToList();
        static bool InRange(string range, Version version)
        {
            var bounds = range.Split('-');
            return bounds.Length == 1 ? Version.Parse(bounds[0]) == version : Version.Parse(bounds[0]) <= version && version <= Version.Parse(bounds[1]);
        }
        return references
            .Where(reference => PublishPath.IsMatch(reference.From) && shipped.TryGetValue(reference.Name, out var version) && version != reference.Version)
            .Where(reference => !redirects.Any(redirect => string.Equals(redirect.Name, reference.Name, StringComparison.OrdinalIgnoreCase)
                && redirect.Old is not null && redirect.New is not null && InRange(redirect.Old, reference.Version) && Version.Parse(redirect.New) == shipped[reference.Name]))
            .Select(reference => $"{reference.From} references {reference.Name} {reference.Version}, but the package ships {shipped[reference.Name]} and "
                + $"VsixPublisher.exe.config has no binding redirect for it; .NET Framework will refuse the load.")
            .Distinct().ToList();
    }
}
