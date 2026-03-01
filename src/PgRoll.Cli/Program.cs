using System.CommandLine;
using PgRoll.Cli;
using PgRoll.Cli.Commands;
using PgRoll.Core.Errors;

var g = new GlobalOptions();
var rootCmd = new RootCommand("pgroll-net — zero-downtime PostgreSQL schema migrations for .NET");

rootCmd.AddGlobalOption(g.Connection);
rootCmd.AddGlobalOption(g.Schema);
rootCmd.AddGlobalOption(g.PgrollSchema);
rootCmd.AddGlobalOption(g.LockTimeout);
rootCmd.AddGlobalOption(g.Role);
rootCmd.AddGlobalOption(g.Verbose);

rootCmd.AddCommand(InitCommand.Build(g));
rootCmd.AddCommand(StartCommand.Build(g));
rootCmd.AddCommand(CompleteCommand.Build(g));
rootCmd.AddCommand(RollbackCommand.Build(g));
rootCmd.AddCommand(StatusCommand.Build(g));
rootCmd.AddCommand(ValidateCommand.Build(g));
rootCmd.AddCommand(MigrateCommand.Build(g));
rootCmd.AddCommand(PendingCommand.Build(g));
rootCmd.AddCommand(PullCommand.Build(g));
rootCmd.AddCommand(EfCoreCommand.Build());
rootCmd.AddCommand(BaselineCommand.Build(g));
rootCmd.AddCommand(LatestCommand.Build(g));

try
{
    return await rootCmd.InvokeAsync(args);
}
catch (PgRollException ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Unexpected error: {ex.Message}");
    return 2;
}
