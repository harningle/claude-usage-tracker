// Milestone 1 spike (plan.md section 3). See docs/milestone-1.md for how each command was run.
using HookBench;

return args.FirstOrDefault() switch
{
    "run" => await Bench.RunAsync(Options.Parse(args[1..])),
    "reload" => await Reload.RunAsync(Options.Parse(args[1..])),
    "time-helper" => await HelperTiming.RunAsync(Options.Parse(args[1..])),
    "time-statusline" => await HelperTiming.RunProductionAsync(Options.Parse(args[1..])),
    "real" => await Real.RunAsync(Options.Parse(args[1..])),
    "interactive" => await Interactive.RunAsync(Options.Parse(args[1..])),
    "probe-statusline" when args.Length == 3 => StatuslineProbe.Run(args[1], int.Parse(args[2])),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("""
        usage:
          HookBench run --helper <HelperSpike.exe> --out <scratch dir> [--calls 25] [--repeat 3]
                        [--conditions none,http-up,http-down,http-restart,async-up,async-down] [--claude <claude.exe>]
          HookBench reload --out <scratch dir> [--wait-ms 3000] [--claude <claude.exe>]
          HookBench time-helper --helper <HelperSpike.exe> --out <scratch dir> [--runs 50] [--bash <bash.exe>]
          HookBench time-statusline --helper <ClaudeUsageTracker.Helper.exe> --out <scratch dir> [--runs 50] [--bash <bash.exe>]
          HookBench real --out <scratch dir> [--calls 20] [--cap <calls>] [--conditions none,http-down] [--dry-run true]
          HookBench interactive --out <scratch dir> [--calls 6] [--step-delay-ms 700] [--probe-sleep-ms 1500] [--idle-wait-s 75]
                                [--helper <installed ClaudeUsageTracker.Helper.exe>] [--claude <claude.exe>]
        """);
    return 2;
}
