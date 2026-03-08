using System.CommandLine;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PgRoll.Cli.Commands;

public static class NewCommand
{
    private static readonly Regex PrefixPattern = new(@"^(\d+)_", RegexOptions.Compiled);

    private static readonly (string Type, string Description)[] OperationTypes =
    [
        ("create_table",      "Create a new table with columns"),
        ("drop_table",        "Drop an existing table"),
        ("rename_table",      "Rename a table"),
        ("add_column",        "Add a column to a table"),
        ("drop_column",       "Drop a column from a table"),
        ("rename_column",     "Rename a column"),
        ("alter_column",      "Change type, nullability, default or rename a column"),
        ("create_index",      "Create an index (CONCURRENTLY)"),
        ("drop_index",        "Drop an index (CONCURRENTLY)"),
        ("create_constraint", "Add a CHECK, UNIQUE or FOREIGN KEY constraint"),
        ("drop_constraint",   "Drop a constraint"),
        ("rename_constraint", "Rename a constraint"),
        ("set_not_null",      "Add NOT NULL to a column"),
        ("drop_not_null",     "Remove NOT NULL from a column"),
        ("set_default",       "Set a column default expression"),
        ("drop_default",      "Drop a column default"),
        ("create_schema",     "Create a schema"),
        ("drop_schema",       "Drop a schema"),
        ("create_enum",       "Create an enum type"),
        ("drop_enum",         "Drop an enum type"),
        ("create_view",       "Create a view"),
        ("drop_view",         "Drop a view"),
        ("raw_sql",           "Execute arbitrary SQL"),
    ];

    public static Command Build()
    {
        var nameArg = new Argument<string?>("name", () => null, "Migration name (optional — you will be prompted if omitted).");
        var outputOpt = new Option<string?>("--output", "Directory where the file is created (default: current directory).");

        var cmd = new Command("new", "Interactively scaffold a new migration file with an auto-incremented numeric prefix.");
        cmd.AddArgument(nameArg);
        cmd.AddOption(outputOpt);

        cmd.SetHandler((name, output) =>
        {
            var dir = output is not null ? Path.GetFullPath(output) : Directory.GetCurrentDirectory();
            Directory.CreateDirectory(dir);

            // ── Ask name if not provided ─────────────────────────────────────
            if (string.IsNullOrWhiteSpace(name))
            {
                Console.Write("Migration name: ");
                name = Console.ReadLine()?.Trim();
                if (string.IsNullOrWhiteSpace(name))
                    throw new InvalidOperationException("Migration name is required.");
            }

            // ── Compute auto-incremented prefix ──────────────────────────────
            var next = NextSequenceNumber(dir);
            var migrationName = $"{next:D4}_{name}";
            var filePath = Path.Combine(dir, $"{migrationName}.json");

            if (File.Exists(filePath))
                throw new InvalidOperationException($"File already exists: {filePath}");

            // ── Interactive operation wizard ──────────────────────────────────
            Console.WriteLine();
            Console.WriteLine("Add operations to this migration (press Enter to skip optional fields).");

            var operations = new List<Dictionary<string, object?>>();

            while (true)
            {
                Console.WriteLine();
                var prompt = operations.Count == 0 ? "Add an operation?" : "Add another operation?";
                if (!AskYesNo(prompt, defaultYes: true))
                    break;

                var op = PromptOperation();
                if (op is not null)
                    operations.Add(op);
            }

            // ── Write file ───────────────────────────────────────────────────
            var migration = new Dictionary<string, object>
            {
                ["name"] = migrationName,
                ["operations"] = operations,
            };

            var json = JsonSerializer.Serialize(migration, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json + Environment.NewLine);

            Console.WriteLine();
            Console.WriteLine($"Created: {filePath}");
        }, nameArg, outputOpt);

        return cmd;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static int NextSequenceNumber(string dir)
    {
        var max = 0;
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var m = PrefixPattern.Match(Path.GetFileName(file));
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n > max)
                max = n;
        }
        return max + 1;
    }

    private static bool AskYesNo(string question, bool defaultYes)
    {
        var hint = defaultYes ? "[Y/n]" : "[y/N]";
        Console.Write($"{question} {hint} ");
        var raw = Console.ReadLine()?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(raw) ? defaultYes : raw is "y" or "yes";
    }

    /// <summary>Prompts for a value. Returns null on empty input when not required.</summary>
    private static string? Ask(string prompt, bool required = false)
    {
        while (true)
        {
            Console.Write($"  {prompt}{(required ? "" : " (optional)")}: ");
            var value = Console.ReadLine()?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
            if (!required)
                return null;
            Console.WriteLine($"  ↑ required.");
        }
    }

    private static List<string> AskCommaSeparated(string prompt)
    {
        Console.Write($"  {prompt}: ");
        return Console.ReadLine()
            ?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList() ?? [];
    }

    private static string AskMultiline(string prompt)
    {
        Console.WriteLine($"  {prompt} (end with an empty line):");
        var lines = new List<string>();
        string? line;
        while (!string.IsNullOrWhiteSpace(line = Console.ReadLine()))
            lines.Add(line);
        return string.Join("\n", lines);
    }

    // ── Operation menu ────────────────────────────────────────────────────────

    private static Dictionary<string, object?>? PromptOperation()
    {
        Console.WriteLine();
        Console.WriteLine("  Operation types:");
        for (var i = 0; i < OperationTypes.Length; i++)
            Console.WriteLine($"  {i + 1,2}. {OperationTypes[i].Type,-20}  {OperationTypes[i].Description}");
        Console.WriteLine();

        int choice;
        while (true)
        {
            Console.Write($"  Select (1-{OperationTypes.Length}): ");
            var raw = Console.ReadLine()?.Trim();
            if (int.TryParse(raw, out choice) && choice >= 1 && choice <= OperationTypes.Length)
                break;
            Console.WriteLine("  Invalid choice — enter a number from the list.");
        }

        var type = OperationTypes[choice - 1].Type;
        Console.WriteLine($"  → {type}");
        Console.WriteLine();

        return type switch
        {
            "create_table" => PromptCreateTable(),
            "drop_table" => Simple(type, ("table", true)),
            "rename_table" => Simple(type, ("from", true), ("to", true)),
            "add_column" => PromptAddColumn(),
            "drop_column" => Simple(type, ("table", true), ("column", true)),
            "rename_column" => Simple(type, ("table", true), ("from", true), ("to", true)),
            "alter_column" => PromptAlterColumn(),
            "create_index" => PromptCreateIndex(),
            "drop_index" => Simple(type, ("table", true), ("name", true)),
            "create_constraint" => PromptCreateConstraint(),
            "drop_constraint" => Simple(type, ("table", true), ("name", true)),
            "rename_constraint" => Simple(type, ("table", true), ("from", true), ("to", true)),
            "set_not_null" => Simple(type, ("table", true), ("column", true)),
            "drop_not_null" => Simple(type, ("table", true), ("column", true)),
            "set_default" => PromptSetDefault(),
            "drop_default" => Simple(type, ("table", true), ("column", true)),
            "create_schema" => Simple(type, ("name", true)),
            "drop_schema" => Simple(type, ("name", true)),
            "create_enum" => PromptCreateEnum(),
            "drop_enum" => Simple(type, ("name", true)),
            "create_view" => PromptCreateView(),
            "drop_view" => Simple(type, ("name", true)),
            "raw_sql" => PromptRawSql(),
            _ => null,
        };
    }

    // ── Generic builder ───────────────────────────────────────────────────────

    private static Dictionary<string, object?> Simple(string type, params (string Field, bool Required)[] fields)
    {
        var op = Op(type);
        foreach (var (field, required) in fields)
        {
            var v = Ask(field, required);
            if (v is not null)
                op[field] = v;
        }
        return op;
    }

    private static Dictionary<string, object?> Op(string type) => new() { ["type"] = type };

    // ── Per-operation prompts ─────────────────────────────────────────────────

    private static Dictionary<string, object?> PromptCreateTable()
    {
        var op = Op("create_table");
        op["table"] = Ask("table name", required: true);

        var columns = new List<Dictionary<string, object?>>();
        Console.WriteLine();
        Console.WriteLine("  Define columns:");

        do
        {
            Console.WriteLine();
            var col = new Dictionary<string, object?>();
            col["name"] = Ask("  column name", required: true);
            col["type"] = Ask("  type (e.g. text, integer, uuid, bigserial, timestamp with time zone)", required: true);

            var nullable = Ask("  nullable? [true/false, default true]");
            if (nullable is "false" or "f" or "no" or "n")
                col["nullable"] = false;

            var pk = Ask("  primary_key? [true/false, default false]");
            if (pk is "true" or "t" or "yes" or "y")
                col["primary_key"] = true;

            var unique = Ask("  unique? [true/false, default false]");
            if (unique is "true" or "t" or "yes" or "y")
                col["unique"] = true;

            var def = Ask("  default expression (e.g. now(), 0, gen_random_uuid())");
            if (def is not null)
                col["default"] = def;

            var refs = Ask("  references (e.g. users(id))");
            if (refs is not null)
                col["references"] = refs;

            columns.Add(col);
            Console.WriteLine();
        }
        while (AskYesNo("  Add another column?", defaultYes: false));

        op["columns"] = columns;
        return op;
    }

    private static Dictionary<string, object?> PromptAddColumn()
    {
        var op = Op("add_column");
        op["table"] = Ask("table", required: true);

        Console.WriteLine();
        var col = new Dictionary<string, object?>();
        col["name"] = Ask("column name", required: true);
        col["type"] = Ask("type (e.g. text, integer, boolean, uuid)", required: true);

        var nullable = Ask("nullable? [true/false, default true]");
        if (nullable is "false" or "f" or "no" or "n")
            col["nullable"] = false;

        var pk = Ask("primary_key? [true/false, default false]");
        if (pk is "true" or "t" or "yes" or "y")
            col["primary_key"] = true;

        var unique = Ask("unique? [true/false, default false]");
        if (unique is "true" or "t" or "yes" or "y")
            col["unique"] = true;

        var def = Ask("default expression");
        if (def is not null)
            col["default"] = def;

        op["column"] = col;

        Console.WriteLine();
        Console.WriteLine("  Backfill expressions (needed for zero-downtime when the column has NOT NULL or a computed value):");
        var up = Ask("up expression (value for existing rows, e.g. 'unknown', now())");
        if (up is not null)
            op["up"] = up;

        var down = Ask("down expression (value when rolling back)");
        if (down is not null)
            op["down"] = down;

        return op;
    }

    private static Dictionary<string, object?> PromptAlterColumn()
    {
        var op = Op("alter_column");
        op["table"] = Ask("table", required: true);
        op["column"] = Ask("current column name", required: true);

        Console.WriteLine();
        Console.WriteLine("  Change what? (all optional — press Enter to skip)");

        var name = Ask("new name (rename column)");
        if (name is not null)
            op["name"] = name;

        var dataType = Ask("new data type (e.g. bigint, text)");
        if (dataType is not null)
            op["data_type"] = dataType;

        var notNull = Ask("not_null [true/false]");
        if (notNull is "true" or "t")
            op["not_null"] = true;
        if (notNull is "false" or "f")
            op["not_null"] = false;

        var def = Ask("new default expression");
        if (def is not null)
            op["default"] = def;

        var check = Ask("check expression (e.g. value > 0)");
        if (check is not null)
            op["check"] = check;

        Console.WriteLine();
        Console.WriteLine("  Backfill expressions (needed for zero-downtime type/nullability changes):");

        var up = Ask("up expression (new column value from old, e.g. \"col\"::bigint)");
        if (up is not null)
            op["up"] = up;

        var down = Ask("down expression (old column value from new, for rollback writes)");
        if (down is not null)
            op["down"] = down;

        return op;
    }

    private static Dictionary<string, object?> PromptCreateIndex()
    {
        var op = Op("create_index");
        op["name"] = Ask("index name", required: true);
        op["table"] = Ask("table", required: true);
        op["columns"] = AskCommaSeparated("columns (comma-separated)");

        var unique = Ask("unique? [true/false, default false]");
        if (unique is "true" or "t" or "yes" or "y")
            op["unique"] = true;

        return op;
    }

    private static Dictionary<string, object?> PromptCreateConstraint()
    {
        var op = Op("create_constraint");
        op["table"] = Ask("table", required: true);
        op["name"] = Ask("constraint name", required: true);

        Console.WriteLine("  Constraint types: check | unique | foreign_key");
        op["constraint_type"] = Ask("constraint type", required: true);

        switch (op["constraint_type"] as string)
        {
            case "check":
                op["check"] = Ask("check expression (e.g. age > 0, status IN ('a','b'))", required: true);
                break;

            case "unique":
                op["columns"] = AskCommaSeparated("columns (comma-separated)");
                break;

            case "foreign_key":
                op["columns"] = AskCommaSeparated("columns in this table (comma-separated)");
                op["references_table"] = Ask("referenced table", required: true);
                op["references_columns"] = AskCommaSeparated("referenced columns (comma-separated)");
                break;
        }

        return op;
    }

    private static Dictionary<string, object?> PromptSetDefault()
    {
        var op = Op("set_default");
        op["table"] = Ask("table", required: true);
        op["column"] = Ask("column", required: true);
        op["default"] = Ask("default expression (e.g. now(), 0, 'pending')", required: true);
        return op;
    }

    private static Dictionary<string, object?> PromptCreateEnum()
    {
        var op = Op("create_enum");
        op["name"] = Ask("enum type name", required: true);
        op["values"] = AskCommaSeparated("values (comma-separated, e.g. active,inactive,pending)");
        return op;
    }

    private static Dictionary<string, object?> PromptCreateView()
    {
        var op = Op("create_view");
        op["name"] = Ask("view name", required: true);
        op["definition"] = AskMultiline("SELECT definition");
        return op;
    }

    private static Dictionary<string, object?> PromptRawSql()
    {
        var op = Op("raw_sql");
        op["sql"] = AskMultiline("SQL to execute");

        if (AskYesNo("  Add rollback SQL?", defaultYes: false))
            op["rollback_sql"] = AskMultiline("Rollback SQL");

        return op;
    }
}
