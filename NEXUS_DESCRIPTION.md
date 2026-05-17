# Sts2 Skin Manager — Nexus Mods listing

## Short description (one-liner)
Manage character skin, card skin, and mixed (spine + extras) mods from one in-game panel — hover preview, inline rename, toggle, reorder. Wrapped in a collapsible toggle so it stays out of the way.

## Long description (paste into Nexus Mods description box)

[size=4][b]What this does[/b][/size]

Slay the Spire 2's modding scene has plenty of character skins (Mesugaki for Regent, Booba for Necrobinder, ...), card-art / portrait reskins, and bundles that ship a character spine together with card art (e.g. AncientWaifus). If you have several installed, normally only one wins or you have to shuffle folders. Sts2 Skin Manager lets you keep them all installed and switch them on/off — or combine the parts you like from each — from the Character Select screen.

[size=4][b]Features[/b][/size]

[list]
[*] [b]Auto-detection[/b] — any skin mod whose [color=#ffcc00].pck[/color] overrides character spine paths or card art/portraits is picked up automatically. No registration, no compatibility file.
[*] [b]Character skin dropdown[/b] — pick which variant is active per character right on Character Select. Mixed mods (spine + extras bundled) get a [color=#ffcc00]📦[/color] indicator + per-item tooltip.
[*] [b]Card skin panel[/b] — toggle multiple card-skin packs at once, reorder priority (top wins for overlapping cards) by drag-and-drop or ↑/↓ arrows, with explicit order numbers and a ✓/✗ status icon per row.
[*] [b]Mixed mods panel (new in v0.6.0)[/b] — for mods that bundle a character spine with card art / event scenes. Toggle them independently in their own tab to layer the extras on top of a different main-spine pick. Example: pick Mesugaki for the spine, toggle AncientWaifus here for its card art — the dropdown's spine always wins overlapping paths.
[*] [b]Collapsible Skin Manager toggle[/b] — the whole UI lives inside a single [color=#ffcc00]▶ Skin manager[/color] toggle, collapsed by default so the Character Select screen stays clean. Save / Discard stay alongside the toggle regardless of body state.
[*] [b]Tabbed inner layout[/b] — Card skins and Mixed mods are tabs inside one panel — only one row list visible at a time with a generous content area when expanded.
[*] [b]Hover preview[/b] — see what a skin looks like [i]before[/i] committing. Hover the 👁 icon beside the character dropdown, or hover a card-skin row's label. Reads [color=#ffcc00]preview.png[/color] (or .jpg / .webp) shipped next to the [color=#ffcc00].pck[/color] if the author provided one; otherwise auto-extracts a sensible default from the [color=#ffcc00].pck[/color] (character-select art for characters, first card art for card packs). No live spine swap, no game state mutation.
[*] [b]Rename mods inline[/b] — give any character variant or card skin a friendly display name. Click ✏️, type, ✓ to save (or Enter), ✕ to cancel. The mod ID stays as the unique key under the hood; aliases never break matching. Collisions with other IDs/aliases are rejected inline with a red highlight and tooltip.
[*] [b]Unified Save / Discard[/b] — every panel shares a single Save button. Make any combination of changes, click Save once, one modal, one restart. Discard rolls everything back to the boot state.
[*] [b]Auto-restart via Steam[/b] — confirm the modal and the mod relaunches STS2 through Steam (~5-10s). Cancel keeps your changes queued for next launch.
[*] [b]Configurable overlay position (new in v0.9.0)[/b] — the Character Select overlay is now docked to the [b]top-right[/b] corner by default, dodging the multiplayer lobby panel and other top-left UI the game itself parks there. Install the optional [url=https://www.nexusmods.com/slaythespire2/mods/27]ModConfig[/url] mod and you'll get an in-game dropdown to switch back to the original top-left layout — change applies immediately, no restart.
[*] [b]Multi-language UI[/b] — follows the game's current language. 16 locales bundled, English fallback.
[/list]

[size=4][b]Installation[/b][/size]

[list=1]
[*] Download the latest release zip.
[*] Extract the [color=#ffcc00]Sts2SkinManager[/color] folder into [color=#ffcc00]<Slay the Spire 2 install>/mods/[/color].
[*] First boot does a one-time self-bootstrap (rewrites load order). A 10-second countdown modal offers auto-restart through Steam — confirm to apply.
[*] On Character Select you'll see the [color=#ffcc00]Skin [character]:[/color] dropdown and the [color=#ffcc00]Card skins[/color] panel.
[/list]

[size=4][b]Compatibility[/b][/size]

Any character skin mod that overrides paths under [color=#ffcc00]res://animations/characters/{characterId}/[/color], and any card-art / portrait mod that overrides [color=#ffcc00]card_art/...[/color] or ships [color=#ffcc00]card_portraits/[/color]. Mod authors don't need to do anything special.

[b]Plays nicely with custom-character mods.[/b] Mods that add brand-new characters (e.g. Ryoshu) share the [color=#ffcc00]animations/characters/{char}/[/color] path pattern with skin mods, so an earlier build would intercept their [color=#ffcc00].pck[/color] and the new character would disappear from Character Select. SkinManager now reads the base-game roster from [color=#ffcc00]SlayTheSpire2.pck[/color] and only manages mods that target a [i]base[/i] character — custom characters stay auto-mountable, no conflict.

[b]Blocks forced overrides from non-active skin mods.[/b] Some character skin mods (e.g. [color=#ffcc00]Booba-Necrobinder-Mod[/color]) register Harmony patches that apply scale / position / skeleton overrides to every instance of the base character — even when you've selected a different skin. SkinManager intercepts [color=#ffcc00]ModManager.TryLoadMod[/color] and prevents the [b]DLL[/b] of non-active character mods from loading, so unselected skins can't interfere with the one you actually picked.

[b]For skin authors[/b] — drop a [color=#ffcc00]preview.png[/color] (or .jpg / .jpeg / .webp) next to your [color=#ffcc00].pck[/color] and the manager will use it for hover preview. Optional — the manager auto-extracts one from the [color=#ffcc00].pck[/color] if you don't ship one.

[size=4][b]Limitations[/b][/size]

[list]
[*] [b]A restart is required for the visual change[/b] — STS2's spine actors don't support hot-swapping their data at runtime, so the mod auto-restarts via Steam (~5-10s).
[*] Detection patterns are limited to combat-spine paths (characters) and card art / portrait paths (cards). Icon-only mods aren't picked up.
[*] Encrypted [color=#ffcc00].pck[/color] files aren't supported.
[/list]

[size=4][b]Reporting issues[/b][/size]

If a skin or card mod isn't being detected or is mis-classified, [b]please attach your boot log when reporting[/b]. The log contains a per-mod classification line showing exactly which asset domains the manager saw, which makes diagnosis much faster.

[b]Log location[/b] — [color=#ffcc00]<user_data>/SlayTheSpire2/logs/godot.log[/color] (typically [color=#ffcc00]%APPDATA%\SlayTheSpire2\logs\godot.log[/color] on Windows). Each boot creates a timestamped backup in the same folder; the latest is always [color=#ffcc00]godot.log[/color].

[b]What to look for[/b] — search for lines tagged [color=#ffcc00][Sts2SkinManager][/color]. The detection block lists every mod with the asset domains it matched: [color=#ffcc00]spine[/color], [color=#ffcc00]char_select[/color], [color=#ffcc00]card_art[/color], [color=#ffcc00]card_portraits[/color], [color=#ffcc00]custom_char[/color]. Paste this block (with a few surrounding lines) into your report on [url=https://github.com/ing-gom/Sts2SkinManager/issues]GitHub issues[/url] or in the Nexus comments.

[size=4][b]Source code[/b][/size]

GitHub: [url=https://github.com/ing-gom/Sts2SkinManager]https://github.com/ing-gom/Sts2SkinManager[/url]

MIT licensed. Bug reports and pull requests welcome.

---

## Categories (suggest on Nexus)
- Utilities
- User Interface
- Tools for Modders

## Tags
sts2, skin manager, character skin, card skin, card portraits, mod manager, ui mod, hover preview, rename, runtime switch, steam restart

## Required mods
None. Works standalone.

## Optional dependencies
- Any character skin or card skin mod you want to manage.
- [url=https://www.nexusmods.com/slaythespire2/mods/27]ModConfig[/url] — optional. Without it, the overlay uses the default top-right position. With it installed, you get an in-game dropdown to switch the overlay between top-right and top-left.
