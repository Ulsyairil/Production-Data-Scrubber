# Production Data Scrubber

Clone a production database into a local database, scrub PII per declarative policy, and keep
foreign key integrity.

The alternative today is a pile of one-off scripts: `pg_dump` into a file, open it in a GUI,
search and replace the email column, hope nobody notices the broken foreign keys. That is slow,
unrepeatable and impossible to review.

This tool reads the source schema, orders the tables so parents come before children, copies the
rows while rewriting values, and remaps every foreign key that points at a rewritten key.

## Properties

- **Deterministic.** The same source, policy, seed and salt always produce the same output. A bug
  that reproduces on a scrubbed copy still reproduces for the next developer.
- **Reviewable.** The policy is a YAML file. It goes through code review like any other change.
- **Foreign key safe.** When a primary key is scrubbed, every table that references it is
  remapped consistently.
- **No network calls.** The tool reads one database and writes another. Nothing leaves the
  machine.

## Install

```bash
dotnet tool install --global ulsyairil.ProductionDataScrubber.Cli
```

## Usage

```bash
# Inspect the source schema without writing anything
data-scrubber --source "Host=db;Database=prod;Username=x;Password=y" --dry-run

# Clone and scrub into a local SQLite file
data-scrubber \
  --source "Host=db;Database=prod;Username=x;Password=y" \
  --target "Data Source=./dev.db" \
  --policy samples/policy.yaml

# JSON summary for CI
data-scrubber --source ... --target ... --policy ... --json
```

### Options

| Option | Default | Meaning |
|---|---|---|
| `--source`, `-s` | `Data Source=source.db` | Source connection string. SQLite path or PostgreSQL connection string |
| `--target`, `-t` | `Data Source=scrubbed.db` | Target connection string |
| `--policy`, `-p` | required | Path to the YAML scrub policy. Not needed with `--dry-run` |
| `--seed` | from policy | Seed for the deterministic generators |
| `--salt` | from policy | Salt for the hash strategy |
| `--json` | off | Emit the run summary as JSON |
| `--dry-run` | off | Report the source schema without writing |

Exit codes: `0` success, `2` bad arguments.

## Policy format

```yaml
version: 1
seed: 42
salt: change-me
default-strategy: keep

tables:
  customers:
    row-limit: 10000
    columns:
      id:
        strategy: surrogate
      email:
        strategy: hash
      full_name:
        strategy: fake
      phone:
        strategy: "null"
      date_of_birth:
        strategy: shift
        shift-days: -3650
      notes:
        strategy: drop
```

Quote the `"null"` strategy. Unquoted `null` is a YAML null, which means "no strategy", not
"replace with null".

### Strategies

| Strategy | Effect |
|---|---|
| `keep` | Copy the value unchanged |
| `"null"` | Replace the value with NULL |
| `constant` | Replace with the literal from `value` |
| `hash` | Replace with a deterministic HMAC-SHA256 hash of the value |
| `fake` | Replace with a deterministic value of the same shape |
| `redact` | Keep the shape, hide the identifying part. `shape: email` keeps the domain |
| `shift` | Move a date or timestamp by `shift-days`. Ordering is preserved |
| `surrogate` | Replace the key with a new one and remap every foreign key that points at it |
| `drop` | Do not copy the column at all |

`row-limit` caps how many rows are copied from one table. Useful for tables that are huge and
boring.

### Surrogate keys

`surrogate` is the strategy that keeps the database consistent when the primary key itself
carries information, for example an email address used as a key.

```yaml
tables:
  customers:
    columns:
      id:
        strategy: surrogate
```

The engine assigns a new key to every customer, records the mapping, and writes the mapped value
into `invoices.customer_id`. Foreign keys stay valid.

## Use as a library

```csharp
using ProductionDataScrubber.Core.Connection;
using ProductionDataScrubber.Core.Engine;
using ProductionDataScrubber.Core.Policy;
using ProductionDataScrubber.Core.Schema;
using ProductionDataScrubber.Core.Scrubbing;

var policy = PolicyLoader.Load("policy.yaml");
var scrubber = new DeterministicValueScrubber(policy.Seed, policy.Salt);
var engine = new ScrubEngine(new PostgresSchemaReader(), scrubber);

await using var source = await ConnectionFactory.OpenAsync(DatabaseKind.Postgres, sourceText);
await using var target = await ConnectionFactory.OpenAsync(DatabaseKind.Sqlite, "Data Source=dev.db");

var schema = await engine.ReadSchemaAsync(source);
var report = await engine.RunAsync(source, target, schema, policy);

Console.WriteLine($"{report.TableCount} tables, {report.TotalRowsWritten} rows");
```

## Scope

What it does: read a schema, copy rows, rewrite values, remap keys, write to a target.

What it does not do: no schema translation between database engines. A PostgreSQL source copied to
a SQLite target keeps the column names but the target types are mapped to the SQLite closest
equivalent. That is enough for a local development database, and it is not a schema migration
tool.

## License

MIT
