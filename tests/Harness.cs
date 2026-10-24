using System;
using System.Linq;

namespace Brawler.Tests;

/// <summary>What every test section calls: <see cref="Check"/> prints PASS or FAIL for one check and counts it.</summary>
static class Harness
{
    /// <summary>The command line, for sections that take options (--wav=DIR).</summary>
    public static string[] Args = Array.Empty<string>();

    /// <summary>When set, only checks whose name contains one of these words are run and counted.</summary>
    public static string[] NameFilter;

    public static int Passed, Failed;

    public static void Check(bool cond, string name, string detail = "")
    {
        if (NameFilter != null && !NameFilter.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase))) return;
        Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  (" + detail + ")" : "")}");
        if (cond) Passed++; else Failed++;
    }
}
