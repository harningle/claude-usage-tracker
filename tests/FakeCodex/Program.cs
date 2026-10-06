// A stand-in for codex.exe, used only by the Codex auto-start tests. It takes its instructions from fake.json in the
// CODEX_HOME it was started with, so that every argument and variable is free to be checked as it arrived:
//   events   "completed" (default), "failed" or "none": the events of codex exec --json it prints
//   exit     its exit code (default 0)
//   sleep    seconds it waits before it prints anything
//   linger   seconds a copy of it, started by it, keeps running after it exits, holding its output open
// It appends what it was started with to report.jsonl beside fake.json. Like codex exec, it first reads its input to
// the end, so an input left open makes it wait.
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

if (Environment.GetEnvironmentVariable("FAKE_CODEX_LINGER") is { } linger)
{
    Thread.Sleep(TimeSpan.FromSeconds(double.Parse(linger)));
    return 0;
}

var input = Console.In.ReadToEnd();
var home = Environment.GetEnvironmentVariable("CODEX_HOME")!;
var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(home, "fake.json")))!.AsObject();
Process? lingering = null;
if (settings["linger"] is { } seconds)
{
    // Started without redirection, it inherits this process's output.
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
    start.Environment["FAKE_CODEX_LINGER"] = seconds.ToString();
    lingering = Process.Start(start);
}

File.AppendAllText(Path.Combine(home, "report.jsonl"), JsonSerializer.Serialize(new
{
    args,
    cwd = Environment.CurrentDirectory,
    cwdEntries = Directory.EnumerateFileSystemEntries(Environment.CurrentDirectory).Count(),
    input,
    codexApiKey = Environment.GetEnvironmentVariable("CODEX_API_KEY") is not null,
    openAiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") is not null,
    pid = Environment.ProcessId,
    lingeringPid = lingering?.Id,
}) + "\n");

if (settings["sleep"] is { } sleep)
{
    Thread.Sleep(TimeSpan.FromSeconds(sleep.GetValue<double>()));
}

Console.Out.WriteLine("""{"type":"thread.started","thread_id":"fake-thread"}""");
Console.Out.WriteLine("""{"type":"turn.started"}""");
switch (settings["events"]?.GetValue<string>() ?? "completed")
{
    case "completed":
        Console.Out.WriteLine("""{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"Hello!"}}""");
        Console.Out.WriteLine("""{"type":"turn.completed","usage":{"input_tokens":10,"cached_input_tokens":0,"output_tokens":3}}""");
        break;
    case "failed":
        Console.Out.WriteLine("""{"type":"turn.failed","error":{"message":"fake failure"}}""");
        break;
}

Console.Out.Flush();
return settings["exit"]?.GetValue<int>() ?? 0;
