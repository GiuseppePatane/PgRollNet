using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using Npgsql;
using PgRoll.Cli;
using PgRoll.Cli.Commands;
using PgRoll.Core.Errors;

var g = new GlobalOptions();
var rootCmd = new RootCommand("pgroll-net — zero-downtime PostgreSQL schema migrations for .NET");

rootCmd.AddGlobalOption(g.Connection);
rootCmd.AddGlobalOption(g.Schema);
rootCmd.AddGlobalOption(g.PgrollSchema);
rootCmd.AddGlobalOption(g.LockTimeout);
rootCmd.AddGlobalOption(g.StatementTimeout);
rootCmd.AddGlobalOption(g.BackfillBatchSize);
rootCmd.AddGlobalOption(g.BackfillDelayMs);
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
rootCmd.AddCommand(DoctorCommand.Build(g));
rootCmd.AddCommand(PlanCommand.Build(g));
rootCmd.AddCommand(InspectActiveCommand.Build(g));
rootCmd.AddCommand(NewCommand.Build());

var parser = new CommandLineBuilder(rootCmd)
    .UseDefaults()
    .UseExceptionHandler((ex, ctx) =>
    {
        var message = ex switch
        {
            PgRollException e => e.Message,
            PostgresException e => $"PostgreSQL error ({e.SqlState}): {e.MessageText}",
            InvalidOperationException e => e.Message,
            _ => $"{ex.GetType().Name}: {ex.Message}"
        };
        Console.Error.WriteLine($"Error: {message}");
        ctx.ExitCode = 1;
    })
    .Build();

return await parser.InvokeAsync(args);
