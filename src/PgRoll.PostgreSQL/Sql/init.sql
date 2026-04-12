CREATE SCHEMA IF NOT EXISTS {0};

CREATE TABLE IF NOT EXISTS {0}.migrations (
    schema      TEXT NOT NULL,
    name        TEXT NOT NULL,
    migration   JSONB,
    migration_checksum TEXT,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    parent      TEXT,
    done        BOOLEAN NOT NULL DEFAULT FALSE,
    PRIMARY KEY (schema, name)
);

ALTER TABLE {0}.migrations
    ADD COLUMN IF NOT EXISTS migration_checksum TEXT;

CREATE UNIQUE INDEX IF NOT EXISTS ux_migrations_active_per_schema
    ON {0}.migrations (schema)
    WHERE done = FALSE;
