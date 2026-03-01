CREATE SCHEMA IF NOT EXISTS {0};

CREATE TABLE IF NOT EXISTS {0}.migrations (
    schema      TEXT NOT NULL,
    name        TEXT NOT NULL,
    migration   JSONB,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    parent      TEXT,
    done        BOOLEAN NOT NULL DEFAULT FALSE,
    PRIMARY KEY (schema, name)
);
