# HomeWatch Activity History Architecture

## Scope

This document records the findings from a direct review of the HomeWatch 2 source code and the uploaded runtime database snapshot. It focuses on historical activity retention, ingestion, activity APIs, filtering, pagination, dashboard behaviour, and performance.

## Executive finding

HomeWatch already stores activity in SQLite. The inability to browse farther back is caused primarily by API and UI limits, not by the absence of a database.

The current implementation limits dashboard and device activity to a maximum of 720 hours (30 days), returns only the newest 200 dashboard events, and returns at most 2,000 events for a device. The frontend exposes only fixed hour ranges up to 30 days.

The correct solution is to keep SQLite, add keyset pagination and explicit date ranges, and separate fast summary queries from paged evidence queries.

## Current database model

`HomeWatchDb` uses Entity Framework Core with SQLite and exposes:

- `Devices`
- `Events`
- `Sessions`
- `Alerts`

Current indexes declared in the model:

- unique device MAC address
- unique device IP address
- event timestamp
- event device ID plus timestamp
- alert acknowledged state plus created time

These indexes are a reasonable foundation for chronological and per-device history. Additional indexes should be added only for measured query patterns.

## Ingestion path

`AdGuardImportWorker` runs continuously and polls AdGuard every five seconds. Each poll requests only the newest configured batch, clamped between 10 and 500 query-log rows.

For every returned row, HomeWatch:

1. Parses the domain, client IP, and timestamp.
2. Resolves or creates a device record.
3. Checks for an existing event with the same timestamp, domain, and device.
4. Classifies the domain.
5. Inserts an `ActivityEvent`.
6. Sends adult hits to the adult-session monitor.

### Important ingestion limitations

- The worker does not maintain an AdGuard cursor or request older pages.
- A restart or outage can create a permanent gap if more events occurred than remain in the latest AdGuard batch.
- A busy network can also lose events if more than the batch size arrive between successful polls.
- Duplicate detection currently performs one database `AnyAsync` query per candidate event, which becomes expensive as traffic increases.

### Recommended ingestion changes

- Maintain a persistent high-water mark using timestamp plus a deterministic event fingerprint.
- Page backward through AdGuard until the high-water mark is reached.
- Fetch existing fingerprints for the whole batch in one query instead of issuing one query per row.
- Add a unique event fingerprint column/index so duplicate prevention is enforced by SQLite.
- Record importer gaps and backfill status in a small `ImportCheckpoints` table.

## Retention findings

No code was found that deletes old `Events`, `Sessions`, or `Alerts`. There is no configured retention job in the reviewed source.

Therefore, activity should remain in SQLite once imported. Missing older activity is more likely caused by:

- events never being imported,
- fixed API cutoffs,
- hard result limits,
- or the frontend not offering older ranges.

## Activity-related API behaviour

### `GET /api/dashboard`

Current behaviour:

- defaults to 24 hours,
- clamps the range to 1–720 hours,
- returns only the newest 200 events,
- reports `eventCount` as the number of returned rows rather than the true count for the selected period.

This endpoint mixes summary work and timeline evidence. It should be split so dashboard metrics do not depend on a 200-row evidence page.

### `GET /api/devices`

Current behaviour:

- defaults to 24 hours,
- clamps the range to 30 days,
- loads every event for every device in the selected range into application memory,
- then performs per-device counting and grouping in memory.

This is the largest clear dashboard performance risk. The database should aggregate counts and top domains instead.

### `GET /api/devices/{id}/activity`

Current behaviour:

- defaults to 24 hours,
- clamps the range to 30 days,
- caps results at 2,000 rows,
- has no cursor,
- has no explicit `from`/`to` range,
- calculates summary values from the returned page, not the complete selected range,
- searches with `ToLower().Contains`, which prevents efficient indexed lookup.

### `GET /api/alerts`

Current behaviour:

- returns only the newest 200 alerts,
- has no date range or pagination.

### Export endpoints

The backup/export endpoints should be reviewed separately before they are used for arbitrary historical exports. Historical export should stream rows rather than materialize an unlimited result set.

## Frontend findings

The dashboard, device view, and investigations view expose fixed choices ending at 720 hours (30 days). Device activity requests up to 2,000 rows in a single call. Dashboard and investigation views depend on `/api/dashboard`, so both inherit its 200-event cap.

The browser refreshes active views every ten seconds. This is acceptable for small queries but amplifies expensive endpoints, especially `/api/devices`, which currently loads all events in the selected range.

## Database snapshot note

The uploaded database copy failed SQLite integrity checks. The ZIP contained `homewatch.db`, `homewatch.db-wal`, and `homewatch.db-shm`, and the snapshot was taken from a recently active installation. A copied SQLite database can be inconsistent when the service is still writing or when the database and WAL are not captured as one atomic snapshot.

This result does not prove that the live database is corrupt. Future diagnostics and backups should use SQLite's online backup API or a controlled WAL checkpoint followed by a file copy while the service is stopped.

## Target API design

### Paged historical evidence

Add a general endpoint:

```text
GET /api/activity
```

Parameters:

- `from` — optional inclusive UTC timestamp
- `to` — optional exclusive UTC timestamp
- `deviceId` — optional device filter
- `category` — optional category filter
- `search` — optional domain filter
- `beforeTimestamp` — keyset cursor timestamp
- `beforeId` — keyset cursor event ID
- `pageSize` — default 100, maximum 500

Ordering:

```text
Timestamp DESC, Id DESC
```

Cursor condition:

```text
Timestamp < beforeTimestamp
OR (Timestamp = beforeTimestamp AND Id < beforeId)
```

The response should contain:

- the current page,
- `hasMore`,
- a next cursor,
- selected range metadata,
- no unbounded row collection.

### Summary endpoint

Add a separate endpoint:

```text
GET /api/activity/summary
```

It should return database-computed metrics for the selected range:

- total event count,
- unique domain count,
- active device count,
- category counts,
- blocked count,
- oldest and newest matching timestamps.

Summary queries should not load event entities.

## Index plan

Keep:

- `Events(Timestamp)`
- `Events(DeviceId, Timestamp)`

Add after measuring query plans:

- `Events(Timestamp DESC, Id DESC)` for global keyset paging
- `Events(DeviceId, Timestamp DESC, Id DESC)` for per-device keyset paging
- `Events(Category, Timestamp DESC, Id DESC)` if category filtering is common

Do not add a normal index for `%term%` domain searches. For fast arbitrary substring search, add SQLite FTS5 later. For exact domain and suffix searches, use normalized root-domain columns and targeted indexes.

## Migration requirement

The application currently uses `Database.EnsureCreated()`. `EnsureCreated()` does not evolve an existing schema when model definitions change.

Before adding checkpoint, fingerprint, summary, or archive tables, HomeWatch needs one of these:

1. EF Core migrations with `Database.Migrate()`, preferred; or
2. a versioned startup schema upgrader with explicit idempotent SQL.

Schema versioning is required before long-term historical storage can be considered reliable.

## Recommended implementation sequence

### Phase 1 — Safe historical browsing

- Add `/api/activity` with keyset pagination.
- Add `/api/activity/summary`.
- Add custom `from` and `to` controls.
- Add presets: 24 hours, 7 days, 30 days, 90 days, 1 year, all history.
- Add `Load older` or infinite scrolling.
- Keep pages at 100–250 events.

### Phase 2 — Remove current performance bottlenecks

- Replace `/api/devices` in-memory event loading with SQL aggregation.
- Batch duplicate checks during import.
- Stop using dashboard evidence rows as the source of dashboard totals.
- Debounce or pause automatic refresh while historical pages are being viewed.

### Phase 3 — Reliable backfill and continuity

- Add import checkpoints.
- Page through AdGuard until the checkpoint is reached.
- Detect and report ingestion gaps.
- Add a unique event fingerprint.

### Phase 4 — Database lifecycle

- Introduce schema migrations.
- Add online backup support.
- Add WAL checkpoint and database-health diagnostics.
- Add configurable retention, with unlimited as a supported option.
- Add optional monthly archives only if the live database becomes large enough to justify them.

### Phase 5 — Long-range reporting

- Add daily summary tables only after raw history and paging are stable.
- Update summaries incrementally during ingestion.
- Use summaries for charts and long-range dashboards; use raw events for investigations.

## Acceptance criteria

Historical browsing is complete when:

- the user can select any date range,
- the UI never requests all matching rows at once,
- older pages can be loaded repeatedly until the oldest retained event,
- dashboard totals reflect the full selected period,
- the dashboard remains responsive with millions of events,
- HomeWatch reports the oldest retained event and any known ingestion gaps,
- backups produce a consistent SQLite snapshot.
