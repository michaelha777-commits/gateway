# Activity history API

All timestamps are ISO 8601 values and are normalized to UTC. `from` is inclusive and `to` is exclusive. Omitting both selects all retained history.

## `GET /api/activity`

Returns a bounded evidence page ordered by `(timestamp DESC, id DESC)`.

| Parameter | Meaning |
| --- | --- |
| `from`, `to` | Optional date range. A range with `from >= to` returns HTTP 400. |
| `deviceId` | Optional device UUID. |
| `category` | Optional exact normalized category; `all` is ignored. |
| `search` | Optional case-insensitive domain substring. `%`, `_`, and `\\` are treated literally. |
| `pageSize` | 1–500; defaults to 100. |
| `cursor` | Opaque `nextCursor` returned by the preceding page. Do not construct or modify it. |

The response has `events`, `range`, `generatedAt`, and `page`. If `page.hasMore` is true, pass `page.nextCursor` with exactly the same filters. Keyset pagination avoids the duplicates, skipped rows, and increasing query cost associated with offset pagination while new records arrive.

## `GET /api/activity/summary`

Accepts the same range and filters except cursor/page size. SQLite calculates total event count, unique domains, active devices, blocked count, category counts, and oldest/newest timestamps over the entire selection. No event entities are materialized.

## `GET /api/dashboard`

Accepts `from` and `to` and returns aggregate dashboard counts only. Evidence belongs to `/api/activity`, so dashboard totals never depend on a truncated timeline.

## `GET /api/devices`

Accepts `from` and `to`. Event and per-domain counts are grouped in SQLite. The endpoint returns device identity, range event count, and up to five leading domains from the aggregated result.

## Error and compatibility rules

Invalid timestamps are rejected by ASP.NET Core as HTTP 400. Invalid cursors and inverted ranges return a JSON `error`. Cursors are scoped by caller convention: changing filters between pages is unsupported. API clients should treat cursors as short-lived opaque continuation tokens.


## `GET /api/activity/history-status`

Returns the SQLite event count and oldest/newest stored timestamps plus the `adguard-querylog` checkpoint. `recoveryComplete: false` and `backfillBefore` indicate a bounded backfill will resume on the next importer cycle. The response explicitly reports that history already purged by AdGuard Home cannot be reconstructed.

## Browser filtering model

Dashboard, Devices, and Investigations all translate the same presets—Last hour, Today, Yesterday, 7/30/90 days, Last year, All History, and Custom Range—into UTC `from`/`to` parameters. A custom end date is sent as the exclusive start of the following local day. Device, category, and domain-search filters are retained on cursor requests; a changed filter starts again without a cursor.
