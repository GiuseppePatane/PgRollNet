using System.CommandLine;
using System.Text.Json;

namespace PgRoll.Cli.Commands;

public static class NewCommand
{
    public static Command Build()
    {
        var nameArg   = new Argument<string>("name", "Migration name (used as file name and as the 'name' field in JSON).");
        var outputOpt = new Option<string?>("--output", "Directory where the file is created (default: current directory).");

        var cmd = new Command("new", "Scaffold a new empty migration file.");
        cmd.AddArgument(nameArg);
        cmd.AddOption(outputOpt);

        cmd.SetHandler((name, output) =>
        {
            var dir = output is not null ? Path.GetFullPath(output) : Directory.GetCurrentDirectory();
            Directory.CreateDirectory(dir);

            var filePath = Path.Combine(dir, $"{name}.json");

            if (File.Exists(filePath))
                throw new InvalidOperationException($"File already exists: {filePath}");

            var skeleton = new
            {
                name,
                operations = Array.Empty<object>()
            };

            var json = JsonSerializer.Serialize(skeleton, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            });

            File.WriteAllText(filePath, json + Environment.NewLine);
            Console.WriteLine(filePath);
        }, nameArg, outputOpt);

        return cmd;
    }
}
