# Convenient Chests 2 Controller Support

Experimental controller compatibility add-on for **Convenient Chests 2 v2.0.4**.

This is a separate SMAPI mod. It does not replace or modify the Convenient Chests 2 installation.

## Intended controller behavior

- D-pad / left stick: move focus through Convenient Chests controls.
- Confirm/action: activate the focused control.
- Cancel: back out of a submenu or leave Lock Items edit mode.
- Shoulder buttons: page through supported grids and dropdowns.
- Mouse, touch, and controller-cursor input remain available.

The patch covers the three controls added beside the normal chest buttons:

- Lock Items
- Chest Alias / Note
- Categorize Chest

It also navigates the custom screens opened from those controls, including category item grids, category selection, snapshot controls, confirmation dialogs, and the alias icon picker.

Alias text entry still requires a keyboard. On Steam Deck, the Steam on-screen keyboard can be used after selecting the text field.

## Install on Steam Deck

The included script can build and install the mod locally:

```bash
chmod +x build-and-install.sh
./build-and-install.sh
```

It looks for a modded Stardew Valley install in common Steam Deck locations. To provide the game path manually:

```bash
GAME_PATH='/path/to/Stardew Valley' ./build-and-install.sh
```

The script installs the compiled mod to:

`Stardew Valley/Mods/ConvenientChestsController`

To uninstall, delete only that folder.

## Status

This is an experimental compatibility add-on and currently targets Convenient Chests 2 version 2.0.4. It uses the existing Convenient Chests UI behavior rather than reimplementing chest storage logic.

## Credits

Convenient Chests 2 is by SummerFleur2997 / SummerFleur.

Original Convenient Chests is by aEnigma.
