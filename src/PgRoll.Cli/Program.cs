using System.CommandLine;
using PgRoll.Cli.Commands;
using PgRoll.Core.Errors;

var rootCmd = new RootCommand("pgroll — zero-downtime PostgreSQL schema migrations for .NET");

rootCmd.AddCommand(InitCommand.Build());
rootCmd.AddCommand(StartCommand.Build());
rootCmd.AddCommand(CompleteCommand.Build());
rootCmd.AddCommand(RollbackCommand.Build());
rootCmd.AddCommand(StatusCommand.Build());
rootCmd.AddCommand(ValidateCommand.Build());
rootCmd.AddCommand(MigrateCommand.Build());
rootCmd.AddCommand(PendingCommand.Build());
rootCmd.AddCommand(PullCommand.Build());
rootCmd.AddCommand(EfCoreCommand.Build());

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
