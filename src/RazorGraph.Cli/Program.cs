using RazorGraph.Cli;

// Every command's logic lives in the Commands classes as a named static, not in
// a SetAction lambda. Anonymous blocks compile under unrecoverable names, cannot
// be called from tests, and are invisible to the code graph — this tool could
// not see its own CLI. Each SetAction body is pure plumbing: read the parsed
// values, hand them to the method that does the work. The tree itself is built
// in CliRoot, which also says why its top is not a RootCommand.
var root = CliRoot.Create();
return await root.Parse(args).InvokeAsync();
