using FluentAssertions;
using PgRoll.Core.Operations;
using PgRoll.Core.Schema;

namespace PgRoll.Core.Tests;

public class ConstraintValidationTests
{
    private static SchemaSnapshot BuildSchema(
        string tableName,
        string[] columns,
        (string name, string type, string def)[]? constraints = null)
    {
        var constraintInfos = (constraints ?? [])
            .Select(c => new ConstraintInfo(c.name, c.type, c.def))
            .ToList();

        var table = new TableInfo(
            Schema: "public",
            Name: tableName,
            Columns: columns.Select((c, i) => new ColumnInfo(c, "text", true, null, false, i + 1)).ToList(),
            Indexes: [],
            Constraints: constraintInfos
        );
        return new SchemaSnapshot([table]);
    }

    // ── CreateConstraintOperation ─────────────────────────────────────────────

    [Fact]
    public void CreateConstraint_CheckValid_IsValid()
    {
        var schema = BuildSchema("users", ["id", "age"]);
        var op = new CreateConstraintOperation
        {
            Table = "users",
            Name = "chk_age",
            ConstraintType = "check",
            Check = "age > 0"
        };
        op.Validate(schema).IsValid.Should().BeTrue();
    }

    [Fact]
    public void CreateConstraint_CheckMissingExpression_IsInvalid()
    {
        var schema = BuildSchema("users", ["id", "age"]);
        var op = new CreateConstraintOperation { Table = "users", Name = "chk_age", ConstraintType = "check" };
        op.Validate(schema).IsValid.Should().BeFalse();
    }

    [Fact]
    public void CreateConstraint_UniqueValid_IsValid()
    {
        var schema = BuildSchema("users", ["id", "email"]);
        var op = new CreateConstraintOperation
        {
            Table = "users",
            Name = "uniq_email",
            ConstraintType = "unique",
            Columns = ["email"]
        };
        op.Validate(schema).IsValid.Should().BeTrue();
    }

    [Fact]
    public void CreateConstraint_UniqueMissingColumns_IsInvalid()
    {
        var schema = BuildSchema("users", ["id", "email"]);
        var op = new CreateConstraintOperation { Table = "users", Name = "uniq_email", ConstraintType = "unique" };
        op.Validate(schema).IsValid.Should().BeFalse();
    }

    [Fact]
    public void CreateConstraint_ConstraintAlreadyExists_IsInvalid()
    {
        var schema = BuildSchema("users", ["id", "age"],
            [("chk_age", "c", "CHECK ((age > 0))")]);
        var op = new CreateConstraintOperation
        {
            Table = "users",
            Name = "chk_age",
            ConstraintType = "check",
            Check = "age > 0"
        };
        var result = op.Validate(schema);
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("chk_age");
    }

    [Fact]
    public void CreateConstraint_TableDoesNotExist_IsInvalid()
    {
        var op = new CreateConstraintOperation
        {
            Table = "missing",
            Name = "chk_x",
            ConstraintType = "check",
            Check = "x > 0"
        };
        op.Validate(SchemaSnapshot.Empty).IsValid.Should().BeFalse();
    }

    [Fact]
    public void CreateConstraint_UnknownType_IsInvalid()
    {
        var schema = BuildSchema("users", ["id"]);
        var op = new CreateConstraintOperation { Table = "users", Name = "bad", ConstraintType = "invalid" };
        op.Validate(schema).IsValid.Should().BeFalse();
    }

    // ── DropConstraintOperation ────────────────────────────────────────────────

    [Fact]
    public void DropConstraint_ConstraintExists_IsValid()
    {
        var schema = BuildSchema("users", ["id", "age"],
            [("chk_age", "c", "CHECK ((age > 0))")]);
        var op = new DropConstraintOperation { Table = "users", Name = "chk_age" };
        op.Validate(schema).IsValid.Should().BeTrue();
    }

    [Fact]
    public void DropConstraint_ConstraintDoesNotExist_IsValid()
    {
        // Treat absent constraint as a no-op (idempotent): useful when a prior
        // alter_column.Complete already cascade-dropped the constraint.
        var schema = BuildSchema("users", ["id"]);
        var op = new DropConstraintOperation { Table = "users", Name = "missing_constraint" };
        op.Validate(schema).IsValid.Should().BeTrue();
    }

    [Fact]
    public void DropConstraint_TableDoesNotExist_IsInvalid()
    {
        var op = new DropConstraintOperation { Table = "missing", Name = "chk_x" };
        op.Validate(SchemaSnapshot.Empty).IsValid.Should().BeFalse();
    }

    // ── RenameConstraintOperation ──────────────────────────────────────────────

    [Fact]
    public void RenameConstraint_ValidSourceAndTarget_IsValid()
    {
        var schema = BuildSchema("users", ["id", "age"],
            [("chk_age", "c", "CHECK ((age > 0))")]);
        var op = new RenameConstraintOperation { Table = "users", From = "chk_age", To = "chk_positive_age" };
        op.Validate(schema).IsValid.Should().BeTrue();
    }

    [Fact]
    public void RenameConstraint_SourceDoesNotExist_IsInvalid()
    {
        var schema = BuildSchema("users", ["id"]);
        var op = new RenameConstraintOperation { Table = "users", From = "missing", To = "new_name" };
        op.Validate(schema).IsValid.Should().BeFalse();
    }

    [Fact]
    public void RenameConstraint_TargetAlreadyExists_IsInvalid()
    {
        var schema = BuildSchema("users", ["id"],
            [("chk_a", "c", "CHECK (id > 0)"), ("chk_b", "c", "CHECK (id < 100)")]);
        var op = new RenameConstraintOperation { Table = "users", From = "chk_a", To = "chk_b" };
        var result = op.Validate(schema);
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("chk_b");
    }
}
