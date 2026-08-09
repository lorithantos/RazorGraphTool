using System.CommandLine;
using RazorGraph.Cli;

// Every command's logic lives in the Commands classes as a named static, not in
// a SetAction lambda. Anonymous blocks compile under unrecoverable names, cannot
// be called from tests, and are invisible to the code graph — this tool could
// not see its own CLI. Each SetAction body is pure plumbing: read the parsed
// values, hand them to the method that does the work.
var root = new RootCommand("RazorGraph — queryable code graph of ASP.NET Core Razor apps");
root.Add(BuildCommands.Build());
root.Add(BuildCommands.BuildSolution());
root.Add(QueryCommand.Query());
root.Add(BodyCommands.Body());
root.Add(BodyCommands.BodyDiff());
root.Add(ResearchCommand.Research());
return await root.Parse(args).InvokeAsync();
