namespace PgRoll.Core.Errors;

public class PgRollException : Exception
{
    public PgRollException(string message) : base(message) { }
    public PgRollException(string message, Exception inner) : base(message, inner) { }
}

public sealed class TableDoesNotExistError(string tableName)
    : PgRollException($"Table '{tableName}' does not exist in the schema.");

public sealed class TableAlreadyExistsError(string tableName)
    : PgRollException($"Table '{tableName}' already exists in the schema.");

public sealed class ColumnDoesNotExistError(string tableName, string columnName)
    : PgRollException($"Column '{columnName}' does not exist in table '{tableName}'.");

public sealed class ColumnAlreadyExistsError(string tableName, string columnName)
    : PgRollException($"Column '{columnName}' already exists in table '{tableName}'.");

public sealed class IndexDoesNotExistError(string indexName)
    : PgRollException($"Index '{indexName}' does not exist in the schema.");

public sealed class IndexAlreadyExistsError(string indexName)
    : PgRollException($"Index '{indexName}' already exists in the schema.");

public sealed class InvalidMigrationError(string reason)
    : PgRollException($"Migration is invalid: {reason}");

public sealed class MigrationAlreadyActiveError(string migrationName)
    : PgRollException($"Migration '{migrationName}' is already active.");

public sealed class NoActiveMigrationError()
    : PgRollException("There is no active migration to complete or rollback.");

public sealed class MigrationNameConflictError(string migrationName)
    : PgRollException($"A migration named '{migrationName}' already exists in the migration history.");

public sealed class SchemaNotInitializedError()
    : PgRollException("PgRoll schema not initialized. Run 'pgroll init' first.");

public sealed class TableRenameConflictError(string newName)
    : PgRollException($"Cannot rename table to '{newName}': that name is already taken.");

public sealed class ColumnRenameConflictError(string tableName, string newName)
    : PgRollException($"Cannot rename column to '{newName}' in table '{tableName}': that name is already taken.");

public sealed class InvalidColumnTypeError(string columnType)
    : PgRollException($"Column type '{columnType}' is not valid.");

public sealed class MissingRequiredFieldError(string fieldName)
    : PgRollException($"Required field '{fieldName}' is missing.");

public sealed class MigrationLockError(string schema)
    : PgRollException($"Could not acquire migration lock for schema '{schema}'. Another migration may already be in progress.");

public sealed class EmptyMigrationError()
    : PgRollException("Migration must contain at least one operation.");

public sealed class UnknownOperationTypeError(string operationType)
    : PgRollException($"Unknown operation type '{operationType}'.");

public sealed class ConnectionError(string reason, Exception? inner = null)
    : PgRollException($"Database connection error: {reason}", inner ?? new Exception(reason));

public sealed class MigrationExecutionError(string phase, string reason, Exception? inner = null)
    : PgRollException($"Migration failed during '{phase}': {reason}", inner ?? new Exception(reason));

public sealed class ConstraintAlreadyExistsError(string tableName, string constraintName)
    : PgRollException($"Constraint '{constraintName}' already exists on table '{tableName}'.");

public sealed class ConstraintDoesNotExistError(string tableName, string constraintName)
    : PgRollException($"Constraint '{constraintName}' does not exist on table '{tableName}'.");
