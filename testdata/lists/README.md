# PostgreSQL schema fixture

`postgresql-schema3.sql` is the native Lists schema from SQL baseline commit
`9827c29211ded20543546fe7696c8c28521189cb`. Migration tests populate this schema
without EF, then verify upgrades and subsequent operations. Keep the SQL unchanged.
