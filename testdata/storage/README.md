# PostgreSQL schema fixtures

`postgresql-schema6.sql` is the document schema from SQL baseline commit
`9827c29211ded20543546fe7696c8c28521189cb`. The EF migration tests create it
directly, populate it without EF, then verify migration and subsequent saves.
Keep the fixture unchanged so those tests continue to cover SQL-created data.
