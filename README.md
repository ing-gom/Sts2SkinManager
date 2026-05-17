# Sts2SkinManager

A Slay the Spire 2 mod that manages installed **character skin mods**, **card skin mods** (card portraits / card art), and **mixed mods** (a character spine + card art bundled in one `.pck`) from one in-game panel on the Character Select screen. The whole UI lives inside a collapsible **Skin Manager** toggle so it stays out of the way until you need it.

🇰🇷 [한국어 README](README.ko.md) · 📦 [Nexus Mods page](https://www.nexusmods.com/slaythespire2/mods/866)

![Character Select with skin dropdown, card skin panel, and restart modal](docs/screenshots/character-select.png)

## Features

- **Auto-detection** — **recursively** scans `<sts2>/mods/` (so you can group mods under category folders like `mods/Characters/`, `mods/Artwork/`, `mods/Utility/` and they'll still be picked up) and detects three kinds (`.pck` filenames must be unique across all subfolders — duplicates after the first are skipped with a warning). STS2 itself also walks `mods/` recursively, so DLL+manifest+pck bundles work from any depth — **just remember to delete the original folder at `mods/<modName>/` after relocating it**, otherwise the framework discovers both and reports `DUPLICATE_ID`:
  - **Character skin mods** — `.pck` files containing `res://animations/characters/{character}/...` paths
  - **Card skin mods** — `.pck` files overriding `card_art/...` or shipping `card_portraits/`
  - **Mixed mods** — `.pck` files that bundle a character spine with card art / event scenes (e.g. AncientWaifus)
- **Collapsible Skin Manager toggle** wraps everything — default collapsed so Character Select stays clean. Save / Discard stay alongside the toggle regardless of body state. Inside, tabs switch between:
  - **Character skin dropdown** — pick which variant is active per character. Mixed mods get a `📦` indicator + per-item tooltip
  - **Card skin tab** — toggle individual packs, reorder priority (top wins for overlapping cards), drag-and-drop or ↑/↓ arrows
  - **Mixed mods tab** — toggle mixed mods independently of the dropdown choice. Layer their extras (cards, events) on top of a different main-spine pick; the dropdown's spine always wins overlapping paths
- **Skin preview on hover** — see what a skin looks like *before* committing:
  - Character skin → hover the 👁 icon beside the dropdown
  - Card skin → hover the row's label
  - Sources: `preview.png` next to the `.pck` if provided; otherwise auto-extracted from the `.pck` (character-select art for characters, first card art for card packs). No live spine swap involved.
- **Batch Save / Discard** — both panels share a single Save button. Make all your changes, click Save once, restart once.
- **Auto-restart via Steam** — confirm and the mod relaunches STS2 through Steam (~5-10s). Cancel and the change stays queued until next launch (Discard to fully revert).
- **Configurable overlay position** — defaults to the **top-right** corner of Character Select (dodges the multiplayer lobby panel and other top-left UI the game itself parks there). With [Nexus ModConfig](https://www.nexusmods.com/slaythespire2/mods/27) installed, flip it back to **top-left** (the v0.8 layout) via an in-game dropdown — change applies immediately, no restart.
- **Multi-language UI** — follows the game's current language. 16 languages supported; unsupported fall back to English.

## How it works

A Harmony patch on `ProjectSettings.LoadResourcePack` intercepts mod boot. The manager reads `skin_choices.json`, mounts the chosen character variant before the character actor is instantiated, and writes the chosen card-skin enable/order state to STS2's `settings.save`.

A second Harmony patch on `ModManager.TryLoadMod` blocks the **DLL** of non-active character mods — without this, mods like `Booba-Necrobinder-Mod` register Harmony patches that force-override the character (scale, position, skeleton) regardless of which skin you actually selected. Block list is rebuilt on every boot from `skin_choices.json`; the active variant (and any enabled mixed mod) keeps its DLL.

Changing your selection writes to `skin_choices.json` and pops a 10-second countdown modal. Confirm to auto-restart; cancel keeps the change queued. Discard restores everything to the state STS2 booted with.

## Install

1. Download the latest release zip.
2. Extract the `Sts2SkinManager` folder into `<Slay the Spire 2 install>/mods/`.
3. First boot does a one-time self-bootstrap (rewrites mod load order so the manager loads first). A 10-second countdown modal will offer auto-restart through Steam — confirm to apply. Without this restart, character mods that loaded before SkinManager keep their forced overrides (scale, etc.) for this session.

## Usage

Launch STS2 → Character Select.

- **Top-right** (default; switch to top-left via [ModConfig](https://www.nexusmods.com/slaythespire2/mods/27) if you prefer): `Skin [<character>]:` dropdown. Click a character, pick a variant.
- **Opposite side of dropdown** (left in top-right mode, right in top-left mode): 👁 hover icon (only visible when the selected variant differs from the currently-applied one and a preview is available) → hover to see the skin's character-select art.
- **Below dropdown**: `Card skins (N/M)` panel with check, order number, ↑/↓, drag handle. Toggle, reorder by arrow or drag-and-drop. Hover a row's label to preview the first card art from that pack.
- Make any combination of changes → click **Save** → restart modal appears → **Restart now** or **Restart later** (changes stay queued until next launch).
- **Discard** rolls everything back to the boot state.

### Authoring a preview image (optional, for mod authors)

Drop a `preview.png` (or `.jpg`, `.jpeg`, `.webp`) next to your `.pck`. Sts2SkinManager will pick it up automatically. If you don't ship one, the manager auto-extracts a sensible default from the `.pck` (the character-select PNG for character mods, the first card art for card mods).

`skin_choices.json` lives at `<user_data>/SlayTheSpire2/Sts2SkinManager/`. Direct edits are detected via file watcher and trigger the same modal.

### Sharing your setup (modpack preset)

Every time you Save, Sts2SkinManager also writes a mirror copy of your selections to `<sts2>/mods/Sts2SkinManager/modpack_preset.json`. To share your full modpack:

1. Make sure your latest selection is saved in-game (`Save` button).
2. Zip your entire `<sts2>/mods/` folder (or just the skin/card mods you want to share + the `Sts2SkinManager` folder).
3. Send the zip. Your friend unzips into their `<sts2>/mods/`. On first launch, Sts2SkinManager seeds `skin_choices.json` from the bundled `modpack_preset.json` — your dropdown picks, card skin order, and toggles all apply automatically.

If a mod referenced by the preset isn't installed on the recipient's machine, that selection silently falls back to `default` (the base game art) and the rest is applied normally.

> **For mod releases on Nexus:** the release zip must **not** include `modpack_preset.json`. Otherwise installing the update would overwrite the recipient's existing preset on their first boot after install. The standard release zip (DLL + manifest + README + LICENSE) already excludes it.

## Limitations

- **Restart required.** STS2's character spine actors don't support hot-swapping data; auto-restart through Steam is the practical solution.
- Character detection looks at `res://animations/characters/{character}/...`. Card detection looks at `card_art/...` or `/card_portraits/`. Mods that only swap icons aren't picked up.
- First install needs one extra restart (load-order self-bootstrap).
- Encrypted `.pck` files aren't supported.

## Reporting issues

If a skin or card mod isn't being detected, is being mis-classified, or behaves oddly, **please attach your boot log when reporting**. The log contains a per-mod classification line showing exactly which asset domains the manager saw, which usually pinpoints the root cause without guesswork.

**Where the log lives** — `<user_data>/SlayTheSpire2/logs/godot.log` (typically `%APPDATA%\SlayTheSpire2\logs\godot.log` on Windows). Each boot rotates a timestamped backup into the same folder; the freshest is always `godot.log`.

**What to look for** — search for lines tagged `[Sts2SkinManager]`. The detection block looks like:

```
[Sts2SkinManager] base character roster (5): [defect, ironclad, necrobinder, regent, silent]
[Sts2SkinManager] detected 9 character skin pck(s), 1 card pack pck(s):
[Sts2SkinManager]   [char] MyCharSkin → [defect] spine:42 char_select:7
[Sts2SkinManager]   [cards] MyCardMod (mixed) char_select:12 card_art:73
[Sts2SkinManager] 1 mixed mod(s) — touch both character and card asset domains:
[Sts2SkinManager]   [mixed] MyCardMod char_select:12 card_art:73 (mounted as cards)
[Sts2SkinManager] skipped 2 custom-character mod(s):
[Sts2SkinManager]   [skip] CustomChar → [] card_portraits:184 custom_char:30
```

Paste this block (with a few lines before/after if relevant) into your report on [GitHub issues](https://github.com/ing-gom/Sts2SkinManager/issues) or in the Nexus comments. The labels `spine:N` / `char_select:N` / `card_art:N` / `card_portraits:N` / `custom_char:N` show which asset patterns the scanner matched.

## License

MIT.
