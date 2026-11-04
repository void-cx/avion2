# extract_gamedata

A developer tool that rebuilds the client-derived files in `assets/gamedata` from an installed AION2 client. It is not
part of AVION2: it is not in `Avion2.sln`, and the app does not reference it.

## What it needs

- An installed AION2 client (Global/EU).
- The client's pak AES key and the `.dat` table constants, each in a local file. They never go into the repo.
- A folder with the Oodle and zlib native libraries CUE4Parse loads.
- Only to refresh `schema.json` after a layout change: a `.usmap` type map of the current client. It stays outside
  the repo.

Set these as environment variables, or put them in `tools/extract_gamedata/local.json` (gitignored), for example
`{"AION2_GAME_DIR": "D:/Games/AION2/Aion2", "AION2_PAK_KEY_FILE": "...", "AION2_DAT_CONSTANTS": "...", "AION2_NATIVES_DIR": "..."}`:

| Setting | What |
|---|---|
| `AION2_GAME_DIR` | the client's `Aion2` folder (the one with `Content/Paks`) |
| `AION2_PAK_KEY` or `AION2_PAK_KEY_FILE` | the pak key, or a file that holds it |
| `AION2_DAT_CONSTANTS` | a JSON file with `manifest_hash_key_hex` and `header_xor_constant` |
| `AION2_NATIVES_DIR` | the folder with the Oodle and zlib libraries |
| `AION2_SCHEMA` | optional: another schema file |
| `AVION2_DATAMINE_DIR` | optional: the work folder (default `%LOCALAPPDATA%\AVION2-datamine`) |

## Running it

```bash
dotnet run --project tools/extract_gamedata -- build            # decode, print what changed, stage the files
dotnet run --project tools/extract_gamedata -- build --write    # also copy them into assets/gamedata
dotnet run --project tools/extract_gamedata -- usmap <file>     # rewrite schema.json from a type map
dotnet run --project tools/extract_gamedata -- dump CraftRecipe # decode tables to JSON in the work folder
```

The build checks every table strictly (version, column count against `schema.json`, every byte used). If anything
does not match, it stops and writes nothing to `assets/gamedata`.
