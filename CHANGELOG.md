# Changelog

All notable changes to Sts2SkinManager are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.14.0] - 2026-05-27

### Added — "vanilla body, keep cards" for mixed character+card mods (config-only; UI to follow)
- **Mixed mods that bundle a character reskin *and* custom card art (e.g. [ATA IronClad](https://www.nexusmods.com/slaythespire2/mods/), a RitsuLib/Lance-based mod) can now keep their card art while reverting the *character body* to vanilla.** Previously this combination was impossible: such mods drive both the body and the card art from their own DLL (Harmony path-rewrites into `res://{Mod}/...`), and the card overrides are owned by the same character registration, so the only states were "full mod skin (body + cards)" or "mod disabled (neither)". There was no way to say "I like the custom card art but want the original character".
- **New mechanism — a non-destructive runtime overlay.** When a mod id is listed under the new `_vanilla_body_mods` key in `skin_choices.json`, SkinManager keeps the mod's DLL loaded (so its card art stays) and, on a deferred next-frame timer (after every mod's pck has mounted), builds and mounts a small generated overlay pck that re-points the mod's own character paths back to the vanilla base game:
  - character **scene** remaps (`scenes/creature_visuals/{char}.tscn`, `…/merchant/characters/{char}_merchant.tscn`, `…/rest_site/characters/{char}_rest_site.tscn`, `…/ui/character_icons/{char}_icon.tscn`, `…/character_select/char_select_bg_{char}.tscn`) → the vanilla `res://scenes/…` scene, matched by basename so the differing folder layout (`scenes/character_select/` vs `scenes/screens/char_select/`) is handled automatically;
  - character **image** imports (top-panel icon + outline, char-select portrait, the four multiplayer-hand textures) → the vanilla `.png.import`, which already targets the vanilla `.ctex` shipped in the base pck.
  - Card art paths (`images/atlases/*cards*`, `card_portraits`) are deliberately left untouched, so the mod's DLL keeps injecting its custom card visuals. **The mod's own files are never modified** — the overlay lives under `user_data/Sts2SkinManager/overlays/` and is regenerated each boot.
- **Why redirect the *scene* and not just the spine leaf assets:** overriding only the leaf `.spskel`/`.spatlas`/`.ctex` reverts the texture/skeleton but leaves the mod's scene node transform in place, so the vanilla body renders at the wrong scale. Redirecting at the `.tscn.remap` level loads the complete vanilla scene (node scale included), keeping everything consistent.
- **No dedicated UI yet** — enable per mod by adding its id to `_vanilla_body_mods` in `skin_choices.json` (e.g. `"_vanilla_body_mods": ["ATA_IronClad"]`). A "vanilla body (keep cards)" toggle on the Character Select panel is the planned next step. (This mirrors how EventArt shipped in 0.13.0: backend first, surfaced through existing config, dedicated UI as a follow-up.)

### Fixed — `PckFileExtractor` can now read large packs (e.g. `SlayTheSpire2.pck`)
- **The directory locator now reads the pack-format-v3 header's directory offset first, falling back to the backward tail-scan only when that offset is missing/implausible.** The tail-scan inspected just the last 512 KB, which cannot reach the directory start of the base game pack (15k+ entries → >1 MB directory); `TryReadIndex` returned null for it. This was a no-op before 0.14.0 (nothing read the base pack), but the vanilla-body overlay builder needs the base pack's index to resolve vanilla counterparts. Mod-pack reads are unchanged — the header path resolves to the same directory the scan would have found.

### Internal
- New `PckFileWriter` — a minimal Godot 4.5 PCK (pack format v3) writer (header + 16-byte-aligned data + tail directory, `PACK_REL_FILEBASE`), reverse-engineered and validated against `ATA_IronClad.pck` with a byte-identical 283-entry round-trip before porting to C#.
- New `VanillaBodyOverlayBuilder` — reads a mod pck + the base pck and emits the redirect overlay described above.

## [0.13.0] - 2026-05-26

### Added — event-background / Ancient retexture mods are now a managed mod kind
- **Mods like [AncientRetexture](https://www.nexusmods.com/slaythespire2/mods/) that retexture Neow-equivalent Ancient NPCs (`images/ancients/{name}_placeholder.png`) and event scene backgrounds (`images/events/{event_name}.png`) are now recognised as a first-class managed kind.** Previously, every PCK-path classification branch missed them: no character spine (`animations/characters/{base}/`), no card art (`MegaCrit.Sts2.Core.Models.Cards.`), no card portraits (`/card_portraits/`), no char-select assets. They fell through every branch in `SkinModScanner.Scan` and the manager never surfaced them — the user had no way to toggle or even see them, even though the pck was still being auto-mounted by STS2's normal mod loader. New `EventArtRegex` in `AssetDomainCatalog` matches `images/ancients/|images/events/` (unanchored substring, so namespaced mod paths like `AncientRetexture/images/ancients/...` plus DLL-side path-redirect also match). New `SkinModKind.EventArt` and `UnifiedModCategory.EventArt` extend the classification surface.
- **EventArt mods surface in the "Other Mods" tab as toggleable rows.** No dedicated panel yet — the existing checkbox flow (`_pendingModEnabled` → `mod_list.is_enabled` in `settings.save`) is sufficient for the one-mod-per-event-collection cardinality typical of this kind. If multiple event-art mods with overlapping coverage emerge, a dedicated tab with priority ordering (mirroring Card Skins) is the natural next step. Boot log includes a new `[events] {ModId} event_art:N` line and the unified `[all-mods]` summary uses category label `events`.
- **The collapsible Skin Manager accordion now builds whenever there is anything to manage** — previously, a setup with only event-art mods (no character variants, no card packs, no mixed) would suppress the UI entirely.

### Fixed — `characters.json` under `localization/{lang}/` no longer misclassified as a BaseLib custom-character manifest
- **`CustomCharacterIndicatorRegex` now excludes `characters.json` paths under `localization/{2-4 letter lang}/`** via a negative lookbehind. The previous regex was an unanchored substring match — a mod that ships an event/dialog NPC name dictionary at `MyMod/localization/eng/characters.json` would get a single `custom_char` hit and be silently skipped as a custom-character mod, never reaching downstream classification branches. This was the exact failure mode that hid AncientRetexture from the new EventArt branch on first deploy: `event_art:16` was correctly counted, but `custom_char:1` (from the localization file) won the branch race because `IsCustomCharacterMod` is evaluated before `IsEventArtMod` in `SkinModScanner.Scan`. Real BaseLib registration manifests live at the mod root or under content paths, never under `localization/` — so the exclusion preserves all existing custom-character detection (Watcher, Ryoshu, MzmChar, BaseLib all keep their original signal counts on `Code/Character/` or `CustomCharacterModel` strings).

## [0.12.2] - 2026-05-26

### Fixed — `modpack_preset.json` no longer triggers a "missing the 'id' field" error on every boot
- **Preset file renamed `modpack_preset.json` → `modpack_preset.preset`.** STS2's `ModManager.ReadModsInDirRecursive` walks the entire `mods/` tree and tries to deserialize every `.json` as a mod manifest. Our preset (written next to the DLL by `SkinChoicesConfig.Save()`) was being picked up, failing the manifest schema check, and producing one ERROR line in the log on every game boot:
  ```
  [ERROR] Mod manifest ...\modpack_preset.json is missing the 'id' field! This is not allowed. The mod will not be loaded.
  ```
  Behavior was unaffected — our actual DLL still loaded from `Sts2SkinManager.json` — but the error spammed user logs and surfaced as a recurring report. Switching the extension takes the file out of the framework's manifest scan entirely; the content is still JSON, and `SkinChoicesConfig.Save()` mirrors to the new path on every save as before.
- **Auto-migration on first boot.** Any existing `modpack_preset.json` (either next to the DLL or at the pre-v0.11.6 hardcoded path `mods/Sts2SkinManager/`) is copied to `modpack_preset.preset` and the old file is deleted, so the framework error stops after one more boot. Curators who already redistributed bundles containing `modpack_preset.json` don't need to re-zip — recipient installs auto-migrate on first launch.

## [0.12.1] - 2026-05-20

### Fixed — BaseLib custom-character mods with mod-namespaced asset paths no longer hijacked as base skins
- **Mods like [MzmChar (Wakaba Mutsumi)](https://www.nexusmods.com/slaythespire2/mods/180) that pack assets under `res://{ModId}/characters/...` instead of the STS2-standard `res://animations/characters/{id}/...` are now correctly classified as custom-character mods.** Previously, every PCK-based signal missed them: no `animations/characters/{base}/` paths (so `chars` was empty), no `Code/Character/` or `CustomCharacterModel` strings inside the pck (so `IsCustomCharacterMod` was false), no `card_art/` matches. The scanner fell through with no classification, but the deferred `CharacterIdSuggester` then scored the mod's DLL — which legitimately references the base Ironclad character data as a framework template — at 34/0 dominance and auto-wrote `MzmChar → ironclad` into `_dll_skin_assignments`. The result: whenever the user activated any other Ironclad skin, MzmChar.dll was DLL-blocked and the Wakaba Mutsumi character disappeared from the roster.
- **New `CustomCharacterFrameworkDetector` adds two signals SkinModScanner now consults whenever the PCK-path scan returns no character paths and no card-art hits:**
  1. **Manifest `dependencies: ["BaseLib"]`.** The canonical, author-declared signal — BaseLib is the custom-character framework, and only mods that add new characters depend on it. Both MzmChar.json and Watcher.json declare it.
  2. **DLL byte reference to `CustomCharacterModel` (ASCII or UTF-16 LE).** The framework's base class name. A mod that inherits from it is by definition adding a new character. Defense-in-depth for any future custom-character mod that ships without declaring BaseLib in its manifest.
  Either signal triggers `skippedCustomCharacterMods` — the mod stays auto-mounted by STS2's normal mod loader, never DLL-blocked, never surfaced in the character/card panels where the user could accidentally disable it.
- **Auto-heal extended to existing mis-assignments.** If `_dll_skin_assignments` already contains an entry like `MzmChar → ironclad` from a previous boot, the scanner now also ignores it when the new detector fires — so users hit by this on v0.11.0–0.12.0 self-recover on the next boot. The warning log line names the trigger (`manifest declares BaseLib dependency or DLL references CustomCharacterModel`) so users can find and remove the stale entry from `skin_choices.json`.

## [0.12.0] - 2026-05-19

### Fixed — content mods (Act 4: Final Ascent style) no longer hijacked as character skins
- **Entity-definition rescue for false-positive DLL skin assignments.** v0.11.0–.9 auto-assigned DLL-driven character skin candidates via `CharacterIdSuggester` (byte-frequency scan of the mod's DLL + pck). Content mods like [Act 4: Final Ascent](https://www.nexusmods.com/slaythespire2/mods/37) (Nexus #37, "Adds The Architect as a full Act 4 boss encounter") reuse Defect's spine/sprite assets inside their pck and pass the suggester's dominance ratio — they were written into `_dll_skin_assignments` as defect skins, then DLL-blocked whenever the user picked a non-default Defect skin. New `EntityDefinitionDetector` reads each mod's DLL via `System.Reflection.Metadata` (PE/TypeDef parsing only, no assembly load) and checks whether it defines new `MonsterModel` / `EncounterModel` / `EventModel` / `CardModel` / `PowerModel` / `RelicModel` / `PotionModel` subclasses. Skin mods never extend these bases; content mods almost always do. Any positive signal demotes the assignment from `_dll_skin_assignments` to `_dll_skin_skipped` at `MainFile.Initialize` time — before `SkinModScanner` reads the (now-cleaned) assignment map — so the rescued mod's DLL is no longer blocked starting from the next boot.
- **Auto-suggester now gated by the same signal.** New suspects discovered by `HarmonyPatchInspector` at the deferred 2-second pass are routed through `EntityBasedRescue.TryGateSuspect` before reaching `CharacterIdSuggester`. A mod that defines content entities is added to `_dll_skin_skipped` silently — no restart modal, no false-positive assignment to clean up later.

### Added — "Other Mods" tab (sibling of Card / Mixed tabs)
- **New "Other Mods" tab covers everything not handled by the Card or Mixed tabs.** Surfaces pck-detected character variants (animeDefect), user-skipped DLL mods (Act4FinalAscent after rescue), pending DLL+pck mods awaiting a decision (Hcxmmx_King_Skin pre-confirmation), and any card/mixed mod the user has force-assigned into a character slot via `_dll_skin_assignments`. Pure card and pure mixed mods continue to live in their dedicated tabs.
- **Each row has a checkbox toggle + reclassification dropdown.** Checkbox is the on/off switch for "Skin Manager is managing this mod" — unchecking adds the mod to `_dll_skin_skipped` (its DLL stays loaded, no panel surfaces it). Dropdown picks the explicit assignment when managed: `Auto` (no override, scanner re-classifies) or `Skin for {char}` (force into `_dll_skin_assignments`). Save persists to `skin_choices.json` and triggers the standard restart modal.
- **`SkinModScanner` now honors user overrides for any mod.** `_dll_skin_assignments` previously only injected DLL-only mods (no character pck paths) as Character variants; it now overrides classification for ANY modId — so a user can force a card-art mod into a character skin slot if they want its DLL gated when a different skin for that character is active. `_dll_skin_skipped` now also short-circuits at the scanner stage: skipped mods are excluded from all panels and never DLL-blocked, regardless of pck content.
- **Boot-time `[all-mods]` log.** Every boot prints a one-line summary per tracked mod (`Act4FinalAscent → skipped [content-mod]`, `animeDefect → char→defect`) so users can confirm classifications without opening the UI.

## [0.11.9] - 2026-05-17

### Added — single-source asset catalog + richer per-mod diagnostics
- **All pck path-pattern recognition now lives in `Discovery/AssetDomainCatalog.cs`.** Previously, `SkinModScanner` and `UnclassifiedModInventory` each defined their own copies of the character-spine / card-art / card-portraits regex literals — drift was already starting (UnclassifiedModInventory still required the strict `card_art/` prefix even after v0.11.8 generalised it in SkinModScanner). The catalog exposes a single `ScanPaths()` method returning a `PathScan` record with per-domain hit counts; both consumers now read from it.
- **Per-mod boot log now shows which asset domains were detected.** Each `[char] / [cards] / [skip]` line ends with a compact label like `spine:42 char_select:7 card_art:71 card_portraits:0 custom_char:2` — only non-zero domains appear. Users can now self-diagnose mis-classifications by reading the boot log instead of grepping pck bytes.
- **New `CustomCharacter` indicator domain.** Matches BaseLib-style custom-character signals: `Code/Character/` paths, `CustomCharacterModel` class name, `characters.json` registration manifest. Used by the scanner to skip Watcher-style mods that pack their cards under a private namespace (e.g. `res://Watcher/images/card_portraits/`) but ship a brand-new character — previously those misclassified as base-card portrait redirects (RegentFemPortraits-style) and surfaced in the card-skin panel, where the user could accidentally disable them.
- **New `CharSelectAsset` indicator domain.** Matches `char_select_X.*` files and `animations/character_select/` paths. Doesn't change classification on its own (a mod touching only char-select assets without spine still falls through) but enables the broader mixed-detection rule below.
- **Broadened mixed-mod detection.** Cards-kind mods that also touch character-select assets are now flagged `IsMixed = true` — e.g. `TheDefectCardArtMod` overrides `char_select_bg_defect.tscn` alongside its card portraits. Mount routing is unchanged (still mod_list toggle via `CardPackApplier`); the flag is purely diagnostic.
- **Dedicated `[mixed]` log section.** After the main `[char]` / `[cards]` listing, a new block enumerates every mod with `IsMixed = true` regardless of Kind, with a `(mounted as char|cards)` tag — helps users spot mods most likely to visually conflict with character skins targeting the same base character.

### Fixed
- **Skipped custom-character mods no longer double-report in the forensic forensic log.** `BaseLib`, `Watcher`, etc. were appearing both under `[skip]` (correctly classified as custom-character pattern) and `[unclassified]` (the dll-skin forensic forensic pass didn't know they were already handled). `UnclassifiedModInventory.Build()` now takes the `customCharacterModIds` set and filters them out.

## [0.11.8] - 2026-05-17

### Fixed — card-art mods with non-standard asset paths are now detected
- **Card-art mods that store portraits under arbitrary directory prefixes are now recognised as card skins.** The previous `CardArtBaseOverrideRegex` required the literal `card_art/` segment immediately before the base-game card namespace, which matched RegentCardsAnimeRework (`card_art/MegaCrit.Sts2.Core.Models.Cards.X_card_art.png`) but missed mods that store their portraits elsewhere — most visibly `TheDefectCardArtMod`, which uses `assets/images/cards/MegaCrit.Sts2.Core.Models.Cards.X_portrait.png` and a Harmony-driven DLL to redirect portrait lookups. Those mods fell through both classification branches (no `animations/characters/{base}/`, no path matching the strict regex) so the scanner never added them to its result and the manager UI never showed them; users had no way to toggle them. The regex is now loosened to match the namespace `MegaCrit.Sts2.Core.Models.Cards.` anywhere in the pck — the namespace is specific enough to base-game cards that any pck mentioning it is overriding card visuals.
- **DLL suggester no longer mis-classifies card-art mods as base-character skins.** Card-art mods like `TheDefectCardArtMod` ship a DLL containing every base-game card class name (e.g. `MegaCrit.Sts2.Core.Models.Cards.AdaptiveStrike`) as string literals for redirect targets. The v0.11.0–.4 byte-frequency suggester would see the heavy concentration of `Defect`-prefixed card names and auto-assign the mod as a defect character skin — which would then DLL-block the mod whenever the user picked any other defect skin. With the scanner fix above, these mods now classify as `SkinModKind.Cards` via the pck pass and land in `alreadyDetectedModIds`, so the suggester's `alreadyDetected.Contains` short-circuit at `DllSkinDetectionService.cs:88` skips them naturally. No separate guard code was needed.

## [0.11.7] - 2026-05-17

### Fixed — defense-in-depth against DLL-skin false positives
- **Custom-character mods are now suppressed from byte-frequency auto-suggestion.** v0.11.0–.4 introduced a dual-encoding byte-pattern suggester so DLL-driven character skins (e.g. `Hcxmmx_Touhou_Sakuya_Skin`) could be auto-detected and DLL-blocked while inactive. The suggester has a known failure mode on STS1→STS2 ports: when a mod adds a brand-new character but its DLL references base-game asset paths (e.g. reusing Necrobinder SFX events for stage hooks), the suggester mis-attributes those references to a base character and writes a `_dll_skin_assignments` entry. The next boot's v0.7.0 DLL block then strands the custom character whenever the user picks "default" or any other skin for that base. The detection service now receives the list of custom-character mods identified by the pck scanner (`animations/characters/{non_base}/` paths) and short-circuits before suggesting for any of them.
- **Stale `_dll_skin_assignments` entries are self-healed on next boot.** If `skin_choices.json` already contains a false-positive assignment from a prior session, the pck scanner now detects the contradiction (assignment points to a base character, but the pck adds non-base characters) and ignores the entry with a one-line warning explaining how to remove it manually. Without this, users hit by the v0.11.0–.4 mis-classification would have to edit JSON by hand to recover.
- **Skip log format clarified.** The "skipped custom-character mod" startup log now prints `{modId} → [chars]` instead of a pre-formatted string, so the list of detected character ids is always visible (useful when diagnosing whether a mod was correctly classified as custom-character or not).

## [0.11.6] - 2026-05-15

### Fixed
- **`modpack_preset.json` now resolves relative to the DLL location.** Previously the preset path was hardcoded to `mods/Sts2SkinManager/`, ignoring where the DLL was actually loaded from. Users with the mod installed under a subdirectory (e.g. `mods/utils/Sts2SkinManager/`) would have their preset written to the wrong location, and `SkinChoicesConfig.Save()`'s `Directory.CreateDirectory` mirror branch would silently create a phantom `mods/Sts2SkinManager/` folder as a side-effect. The path is now derived from `typeof(MainFile).Assembly.Location` so nested installs work correctly. A one-time migration block copies any existing preset from the legacy hardcoded path to the new DLL-relative path on first boot; the block is marked with `[NOTE] Migration block` in `MainFile.cs` and can be removed in a future release. Thanks to [@skay138](https://github.com/skay138) for the fix ([#6](https://github.com/ing-gom/Sts2SkinManager/pull/6)).

## [0.11.5] - 2026-05-15

### Fixed
- **Save / Discard panel now appears for character-skin-only setups.** Previously the panel that hosts the Save and Discard buttons was only built when the user had at least one card-skin or mixed-addon mod installed (`SkinSelectorOverlay.cs:277`). Users with only character-skin mods could change the active variant from the dropdown but had no way to commit it — the dirty mark would appear on the dropdown label, but no Save button was visible. The panel is now also built when any character has at least one detected variant.

### Changed
- **Empty TabContainer no longer appears for character-skin-only setups.** When the Save/Discard panel is shown purely because of character-skin variants (no card-skin or mixed-addon mods present), the outer expand/collapse toggle and the empty TabContainer are skipped entirely. Only Save and Discard buttons are placed in the top row, keeping the character select screen clean. Existing setups with card-skin or mixed-addon mods are unaffected.

## [0.11.4] - 2026-05-15

### Changed — character suggestion algorithm rewrite
- **Dual-encoding byte-pattern matching.** v0.11.0–.3 character suggester only scanned ASCII bytes — but every .NET-based mod stores its `CHARACTER.{X}` enum constants and `ResourceLoader.Load("res://...")` path strings as UTF-16 in the assembly user-string heap. Without UTF-16 awareness we were blind to the most reliable signal in any DLL-driven character skin (Hcxmmx_Touhou_Sakuya_Skin.dll has `SILENT` ×2 in UTF-16 and zero ASCII hits).
- **Weighted scoring with dominance rule.** New scoring per character id:
  - `+5` per `characters/{id}/` ASCII match in pck (literal asset override)
  - `+5` per `characters/{id}/` UTF-16 match in dll (ResourceLoader path)
  - `+3` per `{ID_UPPERCASE}` UTF-16 match in dll (CHARACTER.{X} enum)
  - `+1` per `{id}` UTF-16 match in dll (any user-string ref)
  
  Selection requires top score ≥ 4 AND top score ≥ 2× runner-up. ASCII-inside-DLL matches are intentionally not scored (random alignment inside DLL metadata produces noisy hits — Sts2CardAdvisor's DLL has 16-66 ASCII matches per character, would have falsely-classified to "defect" under v0.11.3).
- **Why this scales without per-mod maintenance:** any mod that targets a single base character must reference that character's id somewhere in its IL — as an enum constant, an asset path, or a string literal. Mods that touch every character (advisors, save tools) hit no clear single dominant character. The detection now follows directly from "what does the binary actually reference," not from hand-curated keywords or type whitelists.

### Detection priority (final)
1. Concrete-character patch on `CharacterModel.{Defect/Ironclad/...}` — strongest signal, used as-is.
2. Dual-encoding byte-pattern scan (this release) — handles all mainstream DLL-driven skin patterns.
3. Manifest keyword scan (v0.11.3) — fallback for the tiny fraction of mods whose IL has zero character refs (mods that load asset paths via reflection-built strings).
4. Otherwise: log as ambiguous with manifest excerpt + manual-edit instructions.

## [0.11.3] - 2026-05-15

### Added
- **Manifest keyword fallback for character suggestion.** When the byte-frequency suggester finds zero base-character hits in a mod's pck/dll (common for mods authored outside the English-speaking community — e.g. `Hcxmmx_Touhou_Sakuya_Skin`'s description says "替换静默猎手外观" with no English "silent" anywhere in the binary), Skin Manager now scans the mod's `mod_manifest.json` `name` + `description` against a localized keyword table (English + Simplified/Traditional Chinese + Japanese + Korean) and uses the result as a third-tier fallback.
- **Ambiguous-mod logs now print manifest excerpt.** When all three suggesters fail, the log line includes the mod's manifest `name` and (truncated) `description` so the user can see at a glance what character it likely targets, plus a copy-paste JSON snippet for adding to `_dll_skin_assignments` or `_dll_skin_skipped`.

### Detection priority
1. Concrete-character patch (`CharacterModel.Defect/Ironclad/Necrobinder/Regent/Silent`) — strongest signal, used as-is.
2. Byte-frequency over pck + dll bytes — single dominant base-character ID.
3. Manifest keyword scan — localized character names in `name`/`description`.
4. Otherwise: log as ambiguous with manifest excerpt + manual-edit instructions.

## [0.11.2] - 2026-05-15

### Added
- **Wider Harmony spine whitelist.** Detection now also flags patches on the concrete character subclasses (`MegaCrit.Sts2.Core.Models.Characters.Defect/Ironclad/Necrobinder/Regent/Silent`), `NCharacterSelectScreen`, `NTopBarPortrait`, and `NVfxSpine`. A patch on a concrete character class short-circuits straight to that character — no byte-frequency guess needed.
- **Forensic boot log for unclassified DLL+pck mods.** Every boot, Skin Manager prints a list of mods that ship both a `.dll` and a `.pck` but have neither standard skin paths in their pck nor Harmony patches on a whitelisted type. For each, the byte-frequency suggester also prints its best character guess (or "no hint"). This gives the user a durable trail for DLL-skin patterns we don't yet auto-detect — they can copy the printed JSON snippet into `_dll_skin_assignments` (to manage) or `_dll_skin_skipped` (to silence).
- **Visibility log for existing assignments / skipped.** Every boot lists current `_dll_skin_assignments` and `_dll_skin_skipped` contents so the user doesn't need to grep the JSON to see what Skin Manager believes.

## [0.11.1] - 2026-05-15

### Fixed
- **False positives from sister mods.** The v0.11.0 dll-skin sweep flagged Sts2CardAdvisor (and would have flagged any inggom Sts2-prefixed sister mod) as a possible character skin because it patches `NCharacterSelectButton` for advisor-overlay reasons. Auto-blocking that mod's DLL would silently disable the advisor on the next boot. Mods whose id starts with `Sts2` are now skipped before suggestion, alongside an explicit non-skin allowlist (`BaseLib` etc.).
- **"Restart later" no longer leaves stale assignments.** Cancelling the v0.11 dll-skin restart modal previously kept the auto-written `_dll_skin_assignments` on disk, so the next boot would still apply them. Cancel now reverts only the keys that this session added (pre-existing assignments are preserved).

### Migration note
- If you booted v0.11.0 once with sister mods installed, open `<user_data>/Sts2SkinManager/skin_choices.json` and remove any `_dll_skin_assignments` entries pointing at `Sts2*` mods (e.g. `"Sts2CardAdvisor": "ironclad"`). v0.11.1 won't add them back.

## [0.11.0] - 2026-05-15

### Added
- **DLL-driven character skin auto-detection.** Mods that swap a base character via Harmony patches (rather than overriding `animations/characters/{char}/...` paths in their pck — e.g. Hcxmmx_King_Skin replacing the Regent with Dead Cells' King) are now picked up automatically. After every other mod finishes initializing, Skin Manager walks `Harmony.GetAllPatchedMethods()` and flags any mod whose patches touch character-spine types (`CharacterModel`, `NCreatureVisuals`, `NCharacterSelectButton`, `MegaSprite`, `NMerchantCharacter`, `NRestSiteCharacter`). For each flagged mod, a byte-frequency scan over its pck + dll suggests which base character it targets; high-confidence matches get auto-assigned and a single restart modal lets you toggle them like any other skin from the next boot onward.
- New `_dll_skin_assignments` (modId → base character id) and `_dll_skin_skipped` (mods you marked as not-a-skin) keys in `skin_choices.json`. Once assigned, the v0.7.0 DLL-load block path automatically blocks the DLL when a different variant is selected.
- 16-language coverage for the new `dll_skin_modal_title` / `dll_skin_modal_body_summary` keys (full translations for EN/KO/JA/ZHS/ZHT/DE/FR/ES/IT/PT-BR/PT/PL/RU/TH/TR; ESP shares SPA).

### Internal
- New `Discovery/HarmonyPatchInspector.cs` (whitelist-driven Harmony scan + assembly→modId map), `Discovery/CharacterIdSuggester.cs` (token-boundary base-character-id frequency counter, threshold 3+ hits with margin 2), `Runtime/DllSkinDetectionService.cs` (defers 2 s after Initialize, writes assignments + shows modal). `SkinModScanner.Scan` now accepts an optional assignments dict and injects assigned mods as Character variants even when their pck has zero standard skin paths.

## [0.10.0] - 2026-05-15

### Added
- **Recursive `mods/` scan.** Skin Manager's pck discovery now walks the entire `mods/` tree at any depth, so users can organize pcks under category folders like `mods/Characters/`, `mods/Artwork/`, `mods/Utility/` and they'll still be picked up. Hidden / `__MACOSX` / dotted folders are skipped. Duplicate pck filenames across subfolders log a warning and the first occurrence wins.

### Documentation
- README EN/KO clarify that STS2 itself also walks `mods/` recursively (`ModManager.ReadModsInDirRecursive`), so DLL+manifest+pck bundles work at any depth — **delete the original `mods/<modName>/` folder after relocating**, otherwise the framework discovers both copies and reports `DUPLICATE_ID`.

### Internal
- Auto-junction prototype (PR #2/#3) was reverted in PR #4 once `sts2.dll` decompilation confirmed the framework already handles nested mod folders natively. No runtime shim is needed.

## [0.9.0] - 2026-05-14

### Added
- **Configurable overlay anchor.** The character-select overlay can now be docked to either the **top-left** (original layout) or the **top-right** corner. Switch live via the optional [ModConfig (Nexus #27)](https://www.nexusmods.com/slaythespire2/mods/27) dropdown — *Overlay position (character select)*. Without ModConfig installed, the mod silently uses the default.
- Overlay layout is now **anchor-based** instead of absolute pixel coordinates — stays correctly placed across resolutions and aspect ratios. `AnchorTopLeft` / `AnchorTopRight` helpers in `SkinSelectorOverlay`; `ModConfigBridge.cs` mirrors the reflection-based pattern used by Sts2ShopVarianceTuner (zero hard dependency on ModConfig).

### Changed
- **Overlay default position is now Top Right** (was Top Left in v0.8 and earlier). The change avoids collision with the multiplayer lobby panel and other UI the game itself parks in the top-left corner of Character Select. Users who prefer the original layout can flip it back to Top Left via the ModConfig dropdown — the change applies immediately, no restart required.

## [0.8.0] - 2026-05-13

### Added
- **Modpack preset sharing.** Every Save now mirrors the current selection to `<sts2>/mods/Sts2SkinManager/modpack_preset.json`. To share a full modpack with a friend, zip your `mods/` folder and send it — on first launch their `skin_choices.json` is seeded from the bundled preset, so dropdown picks, card-skin order, and mixed-mod toggles all apply automatically without anyone having to touch the Roaming/AppData folder.
- README "Sharing your setup" section in both EN and KO with a warning that Nexus release zips must not contain `modpack_preset.json`.

### Notes
- Preset seeding only happens when `<user_data>/Sts2SkinManager/skin_choices.json` doesn't exist yet. Existing users with saved choices are unaffected.
- If a preset references a mod that isn't installed on the recipient's machine, that selection falls back to `default` via the existing `SyncAvailableVariants` path. The recipient's `Save` then mirrors the fallback back into the preset, so re-sharing carries the corrected state forward.

## [0.7.0] - 2026-05-13

### Added
- **DLL load blocking for non-active character mods.** Second Harmony patch on `ModManager.TryLoadMod` intercepts and skips DLL load for any character skin mod that isn't the currently selected variant (or an enabled mixed mod). Without this, mods like `Booba-Necrobinder-Mod` would register Harmony patches that force-override `CharacterModel.CreateVisuals` (scale `0.12`, position `(40, -250)`, skeleton swap) on every instance of the base character — even when you'd selected a completely different skin. Block list rebuilds every boot from `skin_choices.json`.
- **Restart modal on first-boot self-bootstrap.** When `LoadOrderEnforcer` reorders `settings.save` to put SkinManager first in the mod list, a 10-second countdown modal now appears (previously only a `Logger.Warn` line). Without restart, this session's character mods that loaded before SkinManager still have their Harmony patches live; the restart is what makes blocking actually take effect.
- 16-language coverage for the new `load_order_modal_title` / `load_order_modal_body` strings.

### Changed
- `RestartCountdownModal.ShowOrReset` accepts optional `titleKey` / `bodyKey` parameters so the same modal infrastructure can carry different copy for the load-order vs. skin-change cases.

## [0.6.0] - 2026-05-13

### Added
- **Mixed-mod awareness.** Mods that bundle a character spine with card art / event scenes (e.g. AncientWaifus) are now detected as mixed. They appear in the character dropdown with a `📦` indicator (selecting one applies spine + extras together) AND in a new "Mixed mods" panel, where you can toggle them independently to layer their extras on top of a different main-spine pick (the dropdown's character pick always wins spine conflicts).
- **Boot mount priority.** Mixed addons are mounted first (in reverse-priority order); the dropdown's main-spine choice is mounted last so it overrides any conflicting paths.
- **Collapsible Skin Manager section.** The whole UI is now wrapped in a single toggle header (`▶ 스킨 매니저`) — collapsed by default to keep Character Select clean. Save / Discard stay visible alongside the toggle no matter what state the body is in.
- **Tabbed inner layout.** Card-skin and mixed-mod sections live as tabs inside one panel — only one row list shows at a time, with a generous content area when expanded.
- 16-language coverage for every new key (mixed panel + skin manager header).

### Changed
- Dropdown item text for mixed mods is prefixed with `📦` and ships an explicit per-item tooltip (`SetItemTooltip`) so popup hover surfaces the explanation reliably.
- Inline help label inside the Mixed mod tab so the explanation is always visible without depending on hover tooltips.

## [0.5.0] - 2026-05-12

### Added
- **Per-mod display aliases.** Rename any character variant or card skin from the in-game panel — click ✏️, type a friendly name, ✓ to save (Enter also saves) or ✕ to cancel. The mod ID stays as the unique key; aliases are cosmetic and never break matching.
- **Alias uniqueness validation.** Names that collide with another mod ID or another alias are rejected inline (red highlight + tooltip explaining the conflict).

### Fixed
- **Custom-character mods are no longer blocked.** Mods that add brand-new characters (e.g. Ryoshu) had their `.pck` auto-mount intercepted because they shared the `animations/characters/{char}/` path pattern with skin mods, causing the character to disappear from Character Select. SkinManager now reads the base-game roster from `SlayTheSpire2.pck` and only manages mods that target a base character; custom-character mods stay auto-mountable.
- The ✏️ rename button stayed enabled after switching to a character that had no detected skin variants.

## [0.4.0] - 2026-05-12

### Added
- Skin preview on hover. Character skins: hover the 👁 icon beside the dropdown. Card skins: hover the row label. Sources: `{ModFolder}/preview.png|jpg|jpeg|webp` (author convention) or auto-extracted from the `.pck` (character-select art for characters, first card art for card packs). 120 ms hide-debounce to handle cursor traversal.
- Godot 4 PCK v2/v3 parser and `.ctex` WebP/PNG decoder so previews work without mounting the `.pck`.

## [0.3.0] - 2026-05-12

### Added
- Unified Save / Discard pattern: character dropdown and card-skin panel share a single Save button; one click, one modal, one restart.
- Drag-and-drop reordering for card skins with on/off ✓/✗ status icons.

### Changed
- "Card packs" naming → "Card skins" across all 16 languages.

## [0.2.1] - 2026-05-12

### Changed
- Card-pack panel UX polish: `ScrollContainer`, collapsible header, explicit order numbers, top-wins priority order with one-shot schema migration.

## [0.2.0] - 2026-05-12

### Added
- Card pack management: auto-detection of card-skin mods, JSON schema for ordering/enabled state, in-game UI panel, and integration with STS2's `settings.save` mod list.

## [0.1.3] - 2026-05-12

### Fixed
- Overlay was lost when the user changed STS2 language at runtime; now re-attached via `LocManager.SubscribeToLocaleChange`.

## [0.1.2] - 2026-05-12

### Changed
- Modal body wording and dropdown placeholder strings clarified across all locales.

## [0.1.1] - 2026-05-12

### Added
- i18n support — UI follows STS2's current language. 16 locales bundled; missing keys fall back to English.

## [0.1.0] - 2026-05-12

### Added
- Initial public release. Detects character-skin `.pck` mods, blocks their auto-mount, and mounts the chosen variant per character via an in-game dropdown on Character Select. Restart confirmation modal with auto-restart through Steam.
