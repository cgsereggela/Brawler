using System;
using System.IO;
using System.Linq;
using Brawler.Logic;

// Usage: dotnet run -- [log.jsonl] [--humans | --bots] [--mode=sim|host|offline] [--cards=current|all|HASH]
//                      [--min=30] [--out=report.md] [--csv=DIR]
string logPath = null, outPath = null, csvDir = null, cards = "current";
var filter = new StatsFilter();
foreach (var a in args)
{
    if (a == "--humans") filter.Bots = false;
    else if (a == "--bots") filter.Bots = true;
    else if (a.StartsWith("--mode=")) filter.Mode = a[7..];
    else if (a.StartsWith("--cards=")) cards = a[8..];
    else if (a.StartsWith("--min=") && int.TryParse(a[6..], out int min)) filter.MinSamples = min;
    else if (a.StartsWith("--out=")) outPath = a[6..];
    else if (a.StartsWith("--csv=")) csvDir = a[6..];
    else if (a is "-h" or "--help")
    {
        Console.WriteLine("dotnet run -- [log.jsonl] [--humans|--bots] [--mode=sim|host|offline] [--cards=current|all|HASH] [--min=N] [--out=report.md] [--csv=DIR]");
        return 0;
    }
    else if (!a.StartsWith("--")) logPath = a;
    else { Console.Error.WriteLine($"unknown option {a}"); return 2; }
}

// Godot's user:// for this project on Windows (project name "Brawler").
logPath ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "Godot", "app_userdata", "Brawler", "stats", "card_stats.jsonl");
if (!File.Exists(logPath))
{
    Console.Error.WriteLine($"no stats log at {logPath}\nplay a match, or fill it with: godot --headless --path godot --fixed-fps 60 -- --simulate=200");
    return 1;
}

var records = StatsJson.ParseLines(File.ReadLines(logPath), out int skipped);
if (skipped > 0) Console.Error.WriteLine($"skipped {skipped} unreadable line(s)");
filter.CardsHash = cards switch
{
    "all" => null,
    "current" => StatsJson.CardsHash(),
    _ => cards,
};

var data = StatsReport.Build(records, filter);
if (data.Matches == 0 && cards == "current" && records.Count > 0)
    Console.Error.WriteLine($"no matches recorded with the current card table ({filter.CardsHash}); " +
                            $"the log has {string.Join(", ", records.Select(r => r.CardsHash).Distinct())}. Try --cards=all");

var md = StatsReport.Markdown(data);
if (outPath != null)
{
    var dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    File.WriteAllText(outPath, md);
    Console.WriteLine($"report written to {Path.GetFullPath(outPath)}");
}
else Console.WriteLine(md);

if (csvDir != null)
{
    Directory.CreateDirectory(csvDir);
    File.WriteAllText(Path.Combine(csvDir, "cards.csv"), StatsReport.CardsCsv(data));
    File.WriteAllText(Path.Combine(csvDir, "pairs.csv"), StatsReport.PairsCsv(data));
    Console.WriteLine($"csv written to {Path.GetFullPath(csvDir)}");
}
return 0;
