# GUI: how the app window talks to the exe

`VramRipperdoc.exe` with no arguments (or `--gui`) starts a small HTTP server on `127.0.0.1` (random port) and opens
`http://127.0.0.1:<port>/?t=<token>` in an app-style browser window (Edge or Chrome `--app=`, else the default
browser). The page is `src/VramRipperdoc/Gui/app.html`, embedded in the exe: one file, inline CSS and JS, no network.
Any other argument keeps the command-line behaviour (`--cli` forces it with no other option).

## Rules

- Every `/api/*` call needs the token: header `X-RipperDoc-Token: <t>`, or `?t=<t>` for the event stream. Wrong or
  missing token: 403. The `Host` header must be `127.0.0.1:<port>` or `localhost:<port>`: else 403.
  (`?t=` works only for `GET /` and `/api/events`; every other call needs the header.)
- Requests and responses are UTF-8 JSON. Errors: status 4xx/5xx with `{"error": "<message a player can read>"}`.
  An empty POST body counts as `{}`. Bodies over 1 MB: 413. Unknown call: 404; wrong method: 405.
- One job at a time. Starting a job while one runs: 409 with `{"error": ...}`. `POST /api/game`, `/api/mo2` and
  `/api/undo` also answer 409 while a job runs.
- The page is served with `Content-Security-Policy: default-src 'self' 'unsafe-inline' data: blob:; frame-ancestors 'none'`
  (inline CSS/JS, data: and blob: URLs and calls to this server work; nothing else is loaded).
- Sizes are bytes (numbers). Times are ISO 8601 strings. Paths are shown as the player knows them
  (`Platform.Shown`), and paths from the player go through `Platform.FromUser`.

## State

`GET /api/state`

```jsonc
{
  "version": "0.5.0",
  "platform": "windows",              // or "wine" (Linux, Steam Deck)
  "toolDir": "D:\\Tools\\VRAM RipperDoc",
  "outDir": "D:\\Tools\\VRAM RipperDoc",   // where reports, the fix log and live sessions are saved (--out)
  "game": { "path": "D:\\Games\\Cyberpunk 2077", "found": true },   // found=false: path is null, ask for the folder
  "fix": { "path": "D:\\Games\\Cyberpunk 2077\\archive\\pc\\mod\\!RipperDoc_Budget.archive", "found": true },
                                      // the budget fix on disk right now (path null when there is none), even before a scan
  "mo2": { "instance": null, "detected": ["C:\\Modding\\MO2"], "underMo2": false },
  "busy": null,                       // or a Job (below)
  "lastJob": null,                    // or { "kind": "scan"|"fix"|"undo"|"live", "ok": true, "message": "...",
                                      //      "finishedAt": "...", "details": { ... } }   (details: see each job)
  "report": null,                     // or the report of the last scan: exactly what Report.WriteJson writes, with at most
                                      // 300 files per mod like the HTML report. Its budget.steps follow keepSharp, and
                                      // budgetArchive/budgetFix change right after a fix or an Undo
  "keepSharp": ["HD_Textures"],       // mods the fix never shrinks: a mod's `package` if it has one, else its `name`
  "live": { "available": true, "why": "", "sessions": [ /* VRAM-RipperDoc-Live.json entries, oldest first */ ] }
}
```

`Job`:

```jsonc
{
  "kind": "scan",                     // "scan" | "fix" | "live"
  "phase": "scanning",                // scan: "vanilla" | "scanning"; fix: "shrinking" | "saving"; live: "waiting" | "measuring"
  "message": "Scanning mods: 120 / 800",   // ready to show
  "done": 120, "total": 800,          // 0 / 0 when there is nothing to count
  "startedAt": "2026-11-16T10:00:00Z",
  "etaSeconds": null,                 // number when known
  "canStop": false,                   // true for "live"
  "live": null                        // live only: { "seconds": 42, "game": 6442450944, "peak": 6979321856, "gpuTotal": 9663676416 }
}
```

## Calls

| Call | Body | Does | Answers |
|---|---|---|---|
| `GET /` | | The app page (needs `?t=`) | HTML |
| `GET /api/state` | | | State |
| `GET /api/events?t=` | | Server-sent events, see below | stream |
| `POST /api/game` | `{"path": "..."}` | Sets the game folder (also accepts the game's exe or any folder inside it), remembers it in `RipperDoc.ini` | State, or 400 if it isn't the game |
| `POST /api/mo2` | `{"path": "..." \| null}` | Sets or clears the Mod Organizer 2 folder (the one with `ModOrganizer.ini`; the ini file itself is accepted too) | State, or 400 |
| `POST /api/keep-sharp` | `{"mods": ["..."]}` | Replaces the keep-sharp list (saved in `RipperDoc.ini`) | State |
| `POST /api/scan` | `{}` | Starts a scan job | 202 `{}` |
| `POST /api/plan` | FixOptions | Works out what a fix would do. Writes nothing. Needs a report | Plan |
| `POST /api/fix` | FixOptions | Starts a fix job (replaces any existing fix) | 202 `{}` |
| `POST /api/undo` | `{}` | Removes the budget fix. Not a job: answers when done | `{"removed": ["...paths"], "message": "..."}` |
| `POST /api/live/start` | `{"label": "before"}` | Starts measuring the game's VRAM (waits for the game) | 202 `{}`, or 400 with why not |
| `POST /api/live/stop` | `{}` | Stops measuring and saves the session | `{}` |
| `POST /api/open` | `{"what": "report" \| "folder" \| "fix" \| "fixlog"}` | Opens the saved HTML report, the tool folder, the fix file's folder, or the fix log | `{}` |
| `POST /api/quit` | `{}` | Stops the exe (after a running job) | `{}` |
| `POST /api/bye` | `{}` | The page says its window is closing (sent on `pagehide`): the exe then quits 4 seconds after the event stream is gone instead of 20. An event stream connecting again (a reload) takes it back | `{}` |

More answers: `scan` 400 when no game folder is set; `plan` and `fix` 400 before the first scan; `fix` 400 when the
choices shrink nothing; `live/stop` answers `{}` also when nothing is measured (it waits up to 5 s for the session to be
saved); `open` 400 when that file doesn't exist yet; `mo2` 400 when the folder has no `ModOrganizer.ini`.

`FixOptions`: `{ "normal": true, "newItems": "none" }` where `newItems` is `"none"`, `"2k"` or `"1k"`.
`normal` = halve normal-play textures until the mods fit; `newItems` = also shrink new clothing, cars and props.
Mods in `keepSharp` are never touched.

`Plan`:

```jsonc
{
  "budget": 4187593113, "current": 6012954214, "afterPlan": 4100000000, "fits": true,   // normal play, as in report.budget
  "normal":     { "count": 212, "saves": 1912954214 },      // what "normal": true would do
  "newItems2k": { "count": 640, "saves": 9000000000 },
  "newItems1k": { "count": 1900, "saves": 11000000000 },
  "chosen":     { "count": 852, "saves": 10912954214, "writeBytes": 2400000000, "seconds": 95,
                  "perMod": [ { "mod": "HD_Textures", "count": 40, "saves": 800000000 } ] },   // for these FixOptions
  "keptSharp":  { "mods": 1, "textures": 12 }                // left alone because of the keep-sharp list
}
```

`fits` is `afterPlan <= budget`. `perMod[].mod` is the name `keepSharp` uses (the mod's `package` if it has one, else
its `name`), so it can be passed to `/api/keep-sharp` as it is. `seconds` is a whole number.

`lastJob.details`:

- scan: `{ "mods": 812, "seconds": 41.2 }`
- fix: `{ "path": "...", "textures": 852, "saved": 10912954214, "skipped": 3, "mo2Mod": false, "nextSteps": ["..."], "skippedFiles": ["..."] }`
  (`nextSteps`: ready-made sentences, e.g. the MO2 "press F5 and tick it" steps; `skippedFiles`: up to 100 "mod: file (why)")
- live: the saved session, plus `"compare": "Change vs \"before\": peak -1.25 GB, steady -1.13 GB"` when there is an
  earlier one, and `"csv"`: the file with every sample
- undo: `{ "removed": ["...paths"] }`
- a failed job has `ok: false`, the reason in `message` and usually empty `details`

## Events

`GET /api/events?t=<t>` is a `text/event-stream`:

- `event: job` with a `Job` as data, about twice a second while a job runs (and at once when a job starts)
- `event: changed` with `{}` whenever the state changed in any other way (job finished, folder set, ...): fetch `/api/state`
- a comment line (`: ping`) every 5 seconds

Right after connecting the stream sends `: connected` and one `changed`, so a page that reconnects catches up. A job
that takes less than half a second may send a single `job` event or none: the `changed` after it is what counts.

The exe quits when no event stream has been connected for 20 seconds and no job is running, so closing the window
closes the tool. A running job always finishes first. Before the first stream ever connects it waits 60 seconds
(a browser that starts cold can be slow).

## Linux

`ripperdoc.sh --gui` runs the exe under Wine with `--gui --gui-port <n> --gui-token <t> --no-open` and opens the page
with the Linux browser itself. `platform` is then `"wine"`; `live.available` is false with a `why` that points at
`3 - Measure VRAM in game.sh`.
