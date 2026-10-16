using Tessera.Smoke;

// tessera-smoke images <dir> [--count 50]
// tessera-smoke model  <models-dir>
// tessera-smoke search <models-dir> [--require-gpu] [--load-from <model-dir>]
// tessera-smoke coreml <tower.onnx> [--batch 8] [--units ALL] [--format MLProgram] [--cache <dir>] [--loads 1] [--label <name>]
// tessera-smoke core   <core-exe> <library-dir> <data-dir> [--models-from <models-dir>] [--embeddings] [--expect-device coreml] [--timeout <s>]
//
// Each command prints what it checked and the numbers, appends a Markdown
// table to $GITHUB_STEP_SUMMARY when it is set, and exits 1 on any failed check.

try
{
    return args switch
    {
        ["images", var dir, ..] => Images.Run(dir, Args.Int(args, "--count", 50)),
        ["model", var models, ..] => await SearchSmoke.ModelAsync(models),
        ["search", var models, ..] => await SearchSmoke.RunAsync(models, args.Contains("--require-gpu"), Args.String(args, "--load-from")),
        ["coreml", var onnx, ..] => CoreMlProbe.Run(
            onnx, Args.Int(args, "--batch", 8),
            Args.String(args, "--units") ?? "ALL", Args.String(args, "--format") ?? "MLProgram",
            Args.String(args, "--cache"), Args.Int(args, "--loads", 1), Args.String(args, "--label")),
        ["core", var exe, var library, var data, ..] => await CoreSmoke.RunAsync(
            exe, library, data,
            Args.String(args, "--models-from"),
            args.Contains("--embeddings"),
            Args.String(args, "--expect-device"),
            TimeSpan.FromSeconds(Args.Int(args, "--timeout", 600))),
        _ => Usage(),
    };
}
catch (Exception e)
{
    Console.Error.WriteLine($"FAIL {e}");
    return 1;
}

static int Usage()
{
    Console.Error.WriteLine("""
        usage:
          tessera-smoke images <dir> [--count 50]
          tessera-smoke model  <models-dir>
          tessera-smoke search <models-dir> [--require-gpu] [--load-from <model-dir>]
          tessera-smoke coreml <tower.onnx> [--batch 8] [--units ALL] [--format MLProgram] [--cache <dir>] [--loads 1] [--label <name>]
          tessera-smoke core   <core-exe> <library-dir> <data-dir> [--models-from <models-dir>] [--embeddings] [--expect-device coreml] [--timeout <s>]
        """);
    return 2;
}
