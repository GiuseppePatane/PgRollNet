namespace PgRoll.Core.Schema;

public sealed record ConstraintInfo(
    string Name,
    string Type,        // "c"=check, "u"=unique, "f"=foreign key
    string Definition   // e.g. "CHECK ((age > 0))"
);
