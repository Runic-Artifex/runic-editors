using System.Collections.Generic;

namespace RunicArtifex.DevKit.VisualStudio.Core;

/// <summary>
/// The command names that open Manage Extensions, tried in order by the former-extension InfoBar's link. Visual Studio
/// 18.12 names it <c>ManageExtensions</c> and rejects <c>Extensions.ManageExtensions</c> as invalid (0.24.0 checklist
/// P3); Visual Studio 17 named it <c>Extensions.ManageExtensions</c>, kept as the fallback for builds that still do.
/// </summary>
public static class ManageExtensionsCommand
{
    public static readonly IReadOnlyList<string> Candidates = new[] { "ManageExtensions", "Extensions.ManageExtensions" };

    /// <summary>Shown when none of <see cref="Candidates"/> runs.</summary>
    public const string Fallback = "Visual Studio could not open Manage Extensions by command. Open it from the Extensions menu: Extensions > Manage Extensions.";
}
