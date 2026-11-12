# content.json

The treasure hunt data that the counter cannot read from the replays: weeks and prizes, odds, Treasure Hours, and
messages shown to every player. The counter (1.5.0 and later) downloads this file from the `main` branch at startup
and then every 2 hours, so a change reaches every player without a new version of the tool.

A copy is built into the exe (used when GitHub cannot be reached), and the last downloaded copy is kept in
`%APPDATA%\ChestCounter\content.json`.

## Before pushing a change

1. **Increase `revision` by 1.** A player's counter only takes a file whose revision is higher than the one it has.
2. **Check the file.** It is validated strictly: if anything is wrong (bad date, unknown field value, missing text),
   the whole file is ignored and the previous one stays in use. To check it locally, run the counter on a test data
   folder with `CHESTCOUNTER_CONTENT_URL` pointing at the file (any URL that serves it, for example
   `python -m http.server`).
3. Dates are UTC (`2026-11-15T14:00:00Z`).

## Notices (messages on top of the window)

A notice shows above every tab of the window and on top of `report.html`, between `from` and `to`.

```json
"notices": [
  {
    "id": "replays-fortnite-1115",
    "level": "warning",
    "from": "2026-11-15T09:00:00Z",
    "to": "2026-11-22T00:00:00Z",
    "maxVersion": "1.5.0",
    "title": {
      "fr": "Coffres non comptés depuis la mise à jour Fortnite de ce matin",
      "en": "Chests not counted since this morning's Fortnite update",
      "es": "Cofres no contados desde la actualización de Fortnite de esta mañana"
    },
    "text": {
      "fr": "Tes parties sont gardées et seront comptées dès la version 1.5.1, en préparation.",
      "en": "Your matches are kept and will be counted as soon as version 1.5.1, in progress.",
      "es": "Tus partidas se conservan y se contarán desde la versión 1.5.1, en preparación."
    },
    "link": "https://github.com/LED-N/Fortnite-ChestCounter/issues"
  }
]
```

| Field | Required | Meaning |
|---|---|---|
| `id` | yes | Lowercase letters, digits and `-` (40 max). Keep it unique: a closed notice is remembered by its id. |
| `level` | yes | `warning` (red, cannot be closed: for a problem) or `info` (gold, the player can close it). |
| `from`, `to` | yes | When it is shown. |
| `text` | yes | `fr`, `en`, `es` (600 characters max each). A missing language falls back to English. |
| `title` | no | Bold first line, `fr` / `en` / `es` (100 characters max). |
| `link` | no | An `https://` page opened by the **More info** button (issue, release...). |
| `minVersion`, `maxVersion` | no | Only the versions in this range see it. A problem fixed in 1.5.1: `"maxVersion": "1.5.0"`. |

At most 10 notices are read. Remove old ones once their `to` date is past.

## Other parts

- `weeks[].prizes[]`: `id` (unique), `kind` (`outfit`, `pickaxe`, `car`, `jam`, `loading`), `name`, `image` (URL; the
  counter downloads `assets/treasures/<file name>` from this repository first), `prizeIds` (the ids carried by the
  golden chest, e.g. `DelilahPrize:dpid_item14`, shown in the report as "Golden chest reward (dpid_itemN)" while unknown).
- `treasureHours[]`: `start`, `end` (24 h max), `boosts` (kind → multiplier, 1 to 100), `confirmed` (false shows
  "expected, not announced yet").
- `odds[]`: `kind`, `oneIn` (1 in N per chest), `weekly`.
