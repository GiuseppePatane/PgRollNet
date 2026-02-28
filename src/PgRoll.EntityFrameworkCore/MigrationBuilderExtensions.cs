using Microsoft.EntityFrameworkCore.Migrations;
using PgRollMigration = PgRoll.Core.Models.Migration;

namespace PgRoll.EntityFrameworkCore;

public static class MigrationBuilderExtensions
{
    /// <summary>
    /// Converts all operations on this <see cref="MigrationBuilder"/> into a pgroll <see cref="PgRollMigration"/>.
    /// Unsupported operations are silently skipped.
    /// </summary>
    public static PgRollMigration ToPgRollMigration(this MigrationBuilder builder, string name) =>
        EfCoreMigrationConverter.Convert(name, builder.Operations).Migration;
}
