using System;
using System.Collections.Generic;
using System.Linq;

namespace RunicArtifex.DevKit.VisualStudio.Core.Tests;

/// <summary>Plain assertions: a failed check throws, which fails the TUnit test.</summary>
internal static class Check
{
    public static void True(bool condition, string because)
    {
        if (!condition) throw new InvalidOperationException(because);
    }

    public static void Equal<T>(T expected, T actual, string because = "")
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, got {actual}. {because}".TrimEnd());
    }

    public static void Same<T>(IEnumerable<T> expected, IEnumerable<T> actual, string because = "")
    {
        var (x, y) = (expected.ToList(), actual.ToList());
        if (!x.SequenceEqual(y))
            throw new InvalidOperationException($"Expected [{string.Join(", ", x)}], got [{string.Join(", ", y)}]. {because}".TrimEnd());
    }
}
