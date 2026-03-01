using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace PgRoll.Cli.EfCore;

/// <summary>
/// A discovered EF Core migration: its name and the list of MigrationOperation objects
/// returned by its Up() method (accessed via the UpOperations public property).
/// </summary>
public sealed record LoadedMigration(string Name, IReadOnlyList<object> UpOperations);

/// <summary>
/// Loads an EF Core migrations assembly in an isolated AssemblyLoadContext so that
/// any EF Core version (7.x, 8.x, 9.x …) can be loaded without conflicting with
/// the host process's own EF Core references.
/// </summary>
public sealed class MigrationAssemblyLoader : IDisposable
{
    private readonly MigrationLoadContext _context;
    private readonly string _assemblyPath;
    private bool _disposed;

    private MigrationAssemblyLoader(string assemblyPath)
    {
        _assemblyPath = Path.GetFullPath(assemblyPath);
        _context = new MigrationLoadContext(_assemblyPath);
    }

    public static MigrationAssemblyLoader Create(string assemblyPath) => new(assemblyPath);

    /// <summary>
    /// Scans the assembly for concrete types that inherit from
    /// <c>Microsoft.EntityFrameworkCore.Migrations.Migration</c>, instantiates each one,
    /// reads the <c>UpOperations</c> property, and returns them sorted by name
    /// (which is the timestamp-based sort order EF Core uses).
    /// </summary>
    public IReadOnlyList<LoadedMigration> LoadMigrations()
    {
        var assembly = _context.LoadFromAssemblyPath(_assemblyPath);

        // GetTypes() throws ReflectionTypeLoadException when some types can't be resolved
        // (e.g. ASP.NET Core Identity framework assemblies not present in the NuGet cache).
        // We only need migration types, which never depend on those problematic types, so
        // we gracefully fall back to the subset of types that loaded successfully.
        Type[] allTypes;
        try
        {
            allTypes = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            allTypes = ex.Types.Where(t => t is not null).ToArray()!;
        }

        var migrationTypes = allTypes
            .Where(t => !t.IsAbstract && IsMigrationType(t))
            .OrderBy(t => GetMigrationId(t) ?? t.Name)   // sort by timestamp-prefixed ID, not class name
            .ToList();

        var result = new List<LoadedMigration>(migrationTypes.Count);

        foreach (var type in migrationTypes)
        {
            object instance;
            try
            {
                instance = Activator.CreateInstance(type)
                    ?? throw new InvalidOperationException($"Activator returned null for {type.FullName}");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Cannot instantiate migration '{type.Name}'. " +
                    $"Make sure the assembly and all its dependencies are in the same directory. " +
                    $"Inner: {ex.Message}", ex);
            }

            var upOperations = ReadUpOperations(type, instance);
            result.Add(new LoadedMigration(type.Name, upOperations));
        }

        return result;
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private static IReadOnlyList<object> ReadUpOperations(Type type, object instance)
    {
        // UpOperations is a public virtual property on the Migration base class.
        // It lazily calls Up(MigrationBuilder) and caches the result.
        var prop = type.GetProperty("UpOperations",
            BindingFlags.Public | BindingFlags.Instance);

        if (prop is null)
            return [];

        var value = prop.GetValue(instance);

        // IReadOnlyList<MigrationOperation> → IEnumerable (non-generic, always works)
        // Then Cast<object>() to get a uniform IEnumerable<object>.
        if (value is System.Collections.IEnumerable enumerable)
            return enumerable.Cast<object>().ToList();

        return [];
    }

    /// <summary>
    /// Walks the inheritance chain looking for
    /// <c>Microsoft.EntityFrameworkCore.Migrations.Migration</c> by name,
    /// so the check works regardless of which assembly version the base type came from.
    /// </summary>
    private static bool IsMigrationType(Type type)
    {
        var current = type.BaseType;
        while (current is not null)
        {
            if (current.Name == "Migration" &&
                current.Namespace == "Microsoft.EntityFrameworkCore.Migrations")
                return true;

            current = current.BaseType;
        }

        return false;
    }

    /// <summary>
    /// Reads the <c>[Migration("20xxxxxx_Name")]</c> attribute Id (the EF Core timestamp key)
    /// used to establish the canonical apply order, regardless of class name.
    /// Returns null if the attribute is absent (should never happen for a real EF Core migration).
    /// </summary>
    private static string? GetMigrationId(Type type)
    {
        foreach (var attr in type.GetCustomAttributesData())
        {
            if (attr.AttributeType.Name == "MigrationAttribute" &&
                attr.AttributeType.Namespace == "Microsoft.EntityFrameworkCore.Migrations" &&
                attr.ConstructorArguments.Count > 0)
            {
                return attr.ConstructorArguments[0].Value as string;
            }
        }
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _context.Unload();
    }

    // ── Inner load context ─────────────────────────────────────────────────────

    /// <summary>
    /// Resolves assembly dependencies by looking in three places, in order:
    /// 1. The same directory as the migrations assembly (works for published/app outputs).
    /// 2. The NuGet global packages cache, using the .deps.json file located next to
    ///    the assembly to discover package versions and relative paths.
    /// 3. Falls back to the default runtime resolution for framework assemblies.
    /// </summary>
    private sealed class MigrationLoadContext : AssemblyLoadContext
    {
        private readonly string _assemblyDirectory;

        // assemblyName (case-insensitive) → absolute path on disk
        private readonly Dictionary<string, string> _nugetIndex;

        public MigrationLoadContext(string assemblyPath)
            : base(isCollectible: true)
        {
            _assemblyDirectory = Path.GetDirectoryName(assemblyPath)!;
            _nugetIndex = BuildNugetIndex(assemblyPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // 1. Check the assembly's own output folder first.
            var candidate = Path.Combine(_assemblyDirectory, $"{assemblyName.Name}.dll");
            if (File.Exists(candidate))
                return LoadFromAssemblyPath(candidate);

            // 2. Check the NuGet global packages cache via the deps.json index.
            if (assemblyName.Name is not null &&
                _nugetIndex.TryGetValue(assemblyName.Name, out var nugetPath) &&
                File.Exists(nugetPath))
                return LoadFromAssemblyPath(nugetPath);

            // 3. Fall back: let the default context handle runtime / framework assemblies.
            return null;
        }

        // ── deps.json → NuGet index ────────────────────────────────────────────

        /// <summary>
        /// Parses the .deps.json file alongside the assembly and builds a map of
        /// assembly name → absolute path in the NuGet global packages cache.
        /// Returns an empty dictionary if the file is missing or cannot be parsed.
        /// </summary>
        private static Dictionary<string, string> BuildNugetIndex(string assemblyPath)
        {
            var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var dir = Path.GetDirectoryName(assemblyPath)!;
            var baseName = Path.GetFileNameWithoutExtension(assemblyPath);
            var depsFile = Path.Combine(dir, $"{baseName}.deps.json");

            if (!File.Exists(depsFile))
                return index;

            var nugetRoot = GetNugetPackagesRoot();
            if (nugetRoot is null)
                return index;

            try
            {
                using var stream = File.OpenRead(depsFile);
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;

                // libraries[pkg] = { "path": "package.name/version", "type": "package" }
                if (!root.TryGetProperty("libraries", out var libraries))
                    return index;

                // Collect package path → relative runtime dll paths from the target section.
                // targets[".NETCoreApp,..."][pkg] = { "runtime": { "lib/.../Foo.dll": {} } }
                var runtimeFiles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                if (root.TryGetProperty("targets", out var targets))
                {
                    foreach (var target in targets.EnumerateObject())
                    {
                        foreach (var lib in target.Value.EnumerateObject())
                        {
                            if (!lib.Value.TryGetProperty("runtime", out var runtimeDlls))
                                continue;

                            var paths = new List<string>();
                            foreach (var dll in runtimeDlls.EnumerateObject())
                                paths.Add(dll.Name); // e.g. "lib/net6.0/Foo.dll"

                            if (paths.Count > 0)
                                runtimeFiles[lib.Name] = paths;
                        }
                        break; // only need the first (and only) target framework
                    }
                }

                foreach (var lib in libraries.EnumerateObject())
                {
                    if (!lib.Value.TryGetProperty("type", out var typeEl) ||
                        typeEl.GetString() != "package")
                        continue;

                    if (!lib.Value.TryGetProperty("path", out var pathEl))
                        continue;

                    var packageRelativePath = pathEl.GetString(); // e.g. "microsoft.efcore.relational/7.0.11"
                    if (packageRelativePath is null)
                        continue;

                    // Get runtime DLL paths for this package from the target section.
                    if (!runtimeFiles.TryGetValue(lib.Name, out var dllPaths))
                        continue;

                    foreach (var dllRelPath in dllPaths)
                    {
                        // Combine: nugetRoot / package.name/version / lib/net6.0/Foo.dll
                        var fullPath = Path.Combine(nugetRoot, packageRelativePath,
                            dllRelPath.Replace('/', Path.DirectorySeparatorChar));

                        if (!File.Exists(fullPath))
                            continue;

                        var assemblyName = Path.GetFileNameWithoutExtension(fullPath);
                        index.TryAdd(assemblyName, fullPath);
                    }
                }
            }
            catch
            {
                // If parsing fails for any reason, silently fall back to directory-only resolution.
            }

            return index;
        }

        private static string? GetNugetPackagesRoot()
        {
            // Respect NUGET_PACKAGES env var, then fall back to the default location.
            var env = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
            if (!string.IsNullOrEmpty(env) && Directory.Exists(env))
                return env;

            var defaultPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget", "packages");

            return Directory.Exists(defaultPath) ? defaultPath : null;
        }
    }
}
