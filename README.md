# SQLST

**SQLST** is a lightweight, fast and safety-first database query and management tool for Windows that works with
**SQL Server, PostgreSQL, MySQL/MariaDB, Oracle and MongoDB** from a single interface. It is built for the
environments where SQL Server Management Studio cannot be installed or feels too heavy: it installs per user without
administrator rights and covers the everyday work — running queries in tabs with schema-aware autocomplete, exploring
and scripting database objects, editing table data, reading execution plans and exporting results to Excel, CSV and
JSON — while adding guard rails and power tools of its own: a **Safe Write** mode that runs data-changing statements
inside a transaction so you can preview the effect before committing, raw values shown exactly as the database
stores them, a live **SQL Profiler** on Extended Events with query-signature grouping and a visual deadlock graph,
a **Full-Text Search** assistant and index wizard, a **Record Map** that walks foreign keys from any row to all of
its related records, schema and data comparison with sync scripts, a visual query designer, a LINQ ⇄ SQL
translator, log-table analysis, built-in REST and SOAP clients, and an optional local AI assistant that runs fully
offline so your data never leaves the machine.

## ⬇ Download

Get the latest installer from **[Releases](https://github.com/ErolCanDemir/SQLST/releases/latest)** —
`SQLST-Kurulum-x.y.z.exe` — and run it. No administrator rights are required. The user guide
(**SQLST-Kullanim-Kilavuzu-x.y.z.pdf**, in Turkish) is attached to the same release and is also in [`docs/`](docs/).

**Requirements:** Windows 10/11 (64-bit). The installer is self-contained; no separate .NET installation is needed.

> The user interface and the user guide are in **Turkish**.

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) on Windows.

```
dotnet build SQLST.slnx
dotnet test SQLST.slnx
```

The application project is `src/SQLST.App` (.NET 10 + WPF, plain ADO.NET — no ORM). The installer is produced by
`installer/derle.ps1` (Inno Setup 6). Some tests connect to a local SQL Server LocalDB instance.

## License

Copyright © 2026 Erolcan Demir. **All rights reserved.** The source code is published for viewing only — see
[LICENSE](LICENSE). You may download and use the released installer; copying, modifying, redistributing or
publishing your own builds or releases of this software is not permitted without written permission.
