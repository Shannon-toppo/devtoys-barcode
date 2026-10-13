using System.Diagnostics;
using System.Text;
using DevToys.Barcode.Helpers;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using ZXing;

namespace DevToys.Barcode.Measure;

internal static class Program
{
    private static int Main(string[] args)
    {
        string command = args.Length > 0 ? args[0] : "matrix";
        string output = args.Length > 1 ? args[1] : "-";
        switch (command)
        {
            case "matrix":
                Matrix(output, args.Skip(2).ToArray());
                return 0;
            case "perf":
                Performance.Run(args.Skip(1).ToArray());
                return 0;
            case "small":
                Performance.Small(args.Skip(1).ToArray());
                return 0;
            case "breakdown":
                Performance.Breakdown(args.Skip(1).ToArray());
                return 0;
            case "corpus":
                Corpus.Run(args.Skip(1).ToArray());
                return 0;
            case "fp":
                FalsePositives.Run(args.Skip(1).ToArray());
                return 0;
            case "dump":
            {
                // dump <dir> <symbol> <degradation> <strength>
                SymbolSpec symbol = Symbols.All.First(x => x.Name == args[2]);
                using Image<Rgba32> source = Symbols.Render(symbol);
                using Image<Rgba32> degraded = Degrade.Apply(args[3], double.Parse(args[4]), source, 1000);
                string file = Path.Combine(output, $"{symbol.Name}-{args[3]}-{args[4]}.png");
                degraded.SaveAsPng(file);
                Console.WriteLine($"{file} {degraded.Width}x{degraded.Height} read: {string.Join(" | ", BarcodeHelper.Read(degraded).Select(r => r.Format + ":" + r.Text))}");
                return 0;
            }

            default:
                Console.Error.WriteLine("usage: matrix [outfile] [degradation] | perf [test-images dir] [runs] | small [test-images dir] [runs] | breakdown [test-images dir] | corpus <outfile> [test-images dir] | fp [multiplier] | dump <dir> <symbol> <degradation> <strength>");
                return 1;
        }
    }

    private static void Matrix(string output, string[] filters)
    {
        string? only = filters.Length > 0 ? filters[0] : null;
        var cells = new List<(SymbolSpec Symbol, Degradation Degradation, double Strength)>();
        foreach (SymbolSpec symbol in Symbols.All)
        {
            foreach (Degradation degradation in Degradation.All)
            {
                if (only is not null && degradation.Name != only)
                {
                    continue;
                }

                foreach (double strength in degradation.Strengths)
                {
                    cells.Add((symbol, degradation, strength));
                }
            }
        }

        // Baseline: the undegraded symbols must all be read.
        foreach (SymbolSpec symbol in Symbols.All)
        {
            using Image<Rgba32> image = Symbols.Render(symbol);
            if (!Passes(image, symbol, out string got))
            {
                Console.Error.WriteLine($"BASELINE FAIL {symbol.Name}: {got}");
            }
        }

        const int Trials = 3;
        var results = new (string Symbol, string Degradation, double Strength, int Passes, int Trials, string Got, long Ms)[cells.Count];
        var sw = Stopwatch.StartNew();
        Parallel.For(0, cells.Count, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            (SymbolSpec symbol, Degradation degradation, double strength) = cells[i];
            bool random = degradation.Name is "noise" or "background" or "blur" or "jpeg";
            int trials = random ? Trials : 1;
            int passes = 0;
            string gotAll = string.Empty;
            long ms = 0;
            using Image<Rgba32> source = Symbols.Render(symbol);
            for (int t = 0; t < trials; t++)
            {
                using Image<Rgba32> degraded = Degrade.Apply(degradation.Name, strength, source, seed: 1000 + (i * 10) + t);
                var cellWatch = Stopwatch.StartNew();
                if (Passes(degraded, symbol, out string got))
                {
                    passes++;
                }

                ms += cellWatch.ElapsedMilliseconds;
                gotAll += (t > 0 ? " // " : string.Empty) + got;
            }

            results[i] = (symbol.Name, degradation.Name, strength, passes, trials, gotAll, ms);
        });
        Console.Error.WriteLine($"matrix done in {sw.Elapsed.TotalSeconds:0.0}s, {results.Sum(r => r.Passes)}/{results.Sum(r => r.Trials)} trials pass, {results.Count(r => r.Passes == r.Trials)}/{results.Length} cells fully pass");

        var text = new StringBuilder();
        text.AppendLine("symbol,degradation,strength,passes,trials,got,ms");
        foreach (var r in results)
        {
            text.AppendLine($"{r.Symbol},{r.Degradation},{r.Strength},{r.Passes},{r.Trials},\"{r.Got.Replace("\"", "'")}\",{r.Ms}");
        }

        if (output == "-")
        {
            Console.Write(text);
        }
        else
        {
            File.WriteAllText(output, text.ToString());
        }

        PrintTable(results.Select(r => (r.Symbol, r.Degradation, r.Strength, r.Passes, r.Trials)).ToList());
    }

    private static void PrintTable(List<(string Symbol, string Degradation, double Strength, int Passes, int Trials)> results)
    {
        Console.WriteLine();
        foreach (Degradation d in Degradation.All)
        {
            if (!results.Any(r => r.Degradation == d.Name))
            {
                continue;
            }

            Console.WriteLine($"{d.Name} ({d.Unit}): {string.Join("  ", d.Strengths)}");
            foreach (SymbolSpec s in Symbols.All)
            {
                string cells = string.Join("  ", d.Strengths.Select(st =>
                {
                    var r = results.First(r => r.Symbol == s.Name && r.Degradation == d.Name && r.Strength == st);
                    return r.Passes == r.Trials ? "PASS" : r.Passes == 0 ? "fail" : $"{r.Passes}/{r.Trials} ";
                }));
                Console.WriteLine($"  {s.Name,-11} {cells}");
            }
        }
    }

    internal static bool Passes(Image<Rgba32> image, SymbolSpec symbol, out string got)
    {
        IReadOnlyList<BarcodeReadResult> found;
        try
        {
            found = BarcodeHelper.Read(image);
        }
        catch (Exception ex)
        {
            got = "EXCEPTION " + ex.GetType().Name + " " + ex.Message;
            return false;
        }

        got = string.Join(" | ", found.Select(f => $"{f.Format}:{f.Text}"));
        return found.Any(f => f.Format == symbol.Format && f.Text == symbol.ExpectedText)
            || (symbol.Format == BarcodeFormat.UPC_A && found.Any(f => f.Format == BarcodeFormat.EAN_13 && f.Text == "0" + symbol.ExpectedText));
    }
}
