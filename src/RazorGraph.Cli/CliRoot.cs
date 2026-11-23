namespace RazorGraph.Cli;

using System.CommandLine;
using System.CommandLine.Help;

/// <summary>
/// The top of the command tree, named for the command people type.
/// </summary>
/// <remarks>
/// Not a RootCommand on purpose. In System.CommandLine 2.0 a RootCommand's
/// name is RootCommand.ExecutableName, which is the file name of
/// Environment.GetCommandLineArgs()[0] and has no setter. Installed as a .NET
/// tool, that is RazorGraph.Cli.dll, so help and usage said "RazorGraph.Cli"
/// where the user had typed "razorgraph". A plain Command takes its name from
/// the constructor; the help and version options a RootCommand would have
/// added are added here instead. What is given up is the [suggest] directive
/// (dotnet-suggest completion), which only a RootCommand can carry.
/// </remarks>
internal static class CliRoot
{
    /// <summary>The tool's command name, as ToolCommandName in the csproj declares it.</summary>
    internal const string CommandName = "razorgraph";

    public static Command Create()
    {
        var root = new Command(CommandName, "RazorGraph — queryable code graph of ASP.NET Core Razor apps");
        root.Options.Add(new HelpOption());
        root.Options.Add(new VersionOption());
        root.Add(BuildCommands.Build());
        root.Add(BuildCommands.BuildSolution());
        root.Add(LuaCommands.BuildLua());
        root.Add(QueryCommand.Query());
        root.Add(BodyCommands.Body());
        root.Add(BodyCommands.BodyDiff());
        root.Add(ResearchCommand.Research());
        return root;
    }
}
