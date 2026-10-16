using System.Globalization;
using System.Text;

namespace Tessera.Smoke;

/// <summary>Checks and numbers for one command, printed as they happen and summarised at the end.</summary>
internal sealed class Report(string title)
{
    private readonly List<(string What, bool Ok, string? Detail)> _checks = [];
    private readonly List<(string Name, string Value)> _numbers = [];

    public int Failures => _checks.Count(c => !c.Ok);

    public bool Check(bool ok, string what, string? detail = null)
    {
        _checks.Add((what, ok, detail));
        Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}{(detail is null ? "" : $" ({detail})")}");
        return ok;
    }

    public void Number(string name, string value)
    {
        _numbers.Add((name, value));
        Console.WriteLine($"     {name}: {value}");
    }

    public void Number(string name, double value, string unit, int decimals = 1) =>
        Number(name, $"{value.ToString($"F{decimals}", CultureInfo.InvariantCulture)} {unit}".TrimEnd());

    public static void Line(string text) => Console.WriteLine($"     {text}");

    /// <summary>Prints the summary, appends it to the job summary, returns the exit code.</summary>
    public int Finish()
    {
        var md = new StringBuilder();
        md.AppendLine(CultureInfo.InvariantCulture, $"### {title}");
        md.AppendLine();
        if (_numbers.Count > 0)
        {
            md.AppendLine("| Measure | Value |");
            md.AppendLine("|---|---|");
            foreach (var (name, value) in _numbers)
            {
                md.AppendLine(CultureInfo.InvariantCulture, $"| {name} | {value} |");
            }

            md.AppendLine();
        }

        md.AppendLine(CultureInfo.InvariantCulture, $"{_checks.Count - Failures} of {_checks.Count} checks passed.");
        foreach (var (what, _, detail) in _checks.Where(c => !c.Ok))
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"- FAIL {what}{(detail is null ? "" : $": {detail}")}");
        }

        md.AppendLine();
        Console.WriteLine();
        Console.WriteLine(md.ToString());

        var summary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (!string.IsNullOrEmpty(summary))
        {
            File.AppendAllText(summary, md.ToString());
        }

        return Failures == 0 ? 0 : 1;
    }
}

internal static class Args
{
    public static string? String(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public static int Int(string[] args, string name, int fallback) =>
        String(args, name) is { } s ? int.Parse(s, CultureInfo.InvariantCulture) : fallback;
}
