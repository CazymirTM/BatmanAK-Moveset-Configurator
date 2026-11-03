# Changelog

## 2.0.0 (2026-10-31)

The biggest Oracle update so far: two new tabs and a reworked glide.

New: CAMERA tab
- SIT & FOV: Field of View for every camera (walking, running, fights, predator, gliding and the Batmobile), Camera Height, Shoulder Offset (left, centred or right) and Aim & Zoom FOV.
- PIVOT & PULL: Camera Distance and Pivot Height.
- GLIDING: Glide FOV, Dive Speed Kick (how much the view widens at speed) and Glide Camera Distance.
- BATMOBILE: Batmobile FOV, Speed FOV Kick and Batmobile Camera Distance.
- TOOLS: nine camera presets (Vanilla, Wide, Ultra Wide, Cinematic, Over Shoulder, Centred, Action, Tactical, Noir), a live FOV readout, Steady Camera (no hip sway), No Screen Shake and Reset Camera.
- Changes apply instantly, also mid-glide, mid-fight and while driving, and come back after loading or dying. Fight FOV and aiming (batarang, line launcher, remote claw, magnetic blast, hacking device, Z zoom) follow the sliders too.

New: FUN tab
- FALL DAMAGE: long falls hurt Batman, and falls from the DEADLY HEIGHT (6 to 80 m, default 20 m) kill him on impact, with no last-second cape save. Short drops are safe. Gliding and grappling still save you, and God Mode overrides it.
- STAMINA: the XP bar becomes a STAMINA bar. Sprinting, gliding, jumping, grappling, attacking and dodging tire Batman out, and fights wear him down. When it runs out he can't run, jump, glide, grapple, attack or dodge until he gets his breath back. Countering always works. STAMINA DRAIN and STAMINA RECOVERY sliders. The bar turns amber when low and red when empty; switching it off brings the XP bar back.

New: INFINITE GLIDE (PLAYER > GLIDING)
- Glide as long as you hold glide, steered with the camera: look up to climb, look ahead to fly level, look down to dive. Normal glide speed and pose.
- GLIDE CLIMB ANGLE slider (8 to 40 degrees).

Other
- 11 tabs: PLAYER, COMBAT, ENEMIES, VISUAL, CAMERA, BATMOBILE, WORLD, CHARACTER, MUSIC, FUN, SYSTEM. The tab bar fits them all on screen.
- New hotkey ids for everything above, listed in mod.toml.

## 1.4.0 (2026-10-29)

New
- RAIN AREA slider (VISUAL > WEATHER): the size of the rain box around you, 25% to 300%. Smaller packs big, dense drops close around the camera; bigger spreads the rain further out and thinner up close.
- Controller: as another character, press LB next to the Batmobile to get in, the same as E on the keyboard.

Improved
- NO RAIN now really turns the rain off. The streets and rooftops dry out (no wet shine, puddles or ripples), suits and the Batmobile dry off, and the rain goes quiet: the rain bed, rain hitting metal, glass and concrete, and rain sound sources placed in the level. VANILLA brings all of it back.
- WET STREETS at 0% now gives completely dry streets (before, they stayed a little damp and dark).

Fixed
- E to get in the Batmobile as another character also works when the game stops sending keys to mods.

## 1.3.1 (2026-10-28)

Fixed
- Teleport to a waypoint far away no longer drops you through the map. Oracle now knows all 65 city districts (read from the game itself, and shipped complete in cells.cache), and it only puts you down on ground the game has really loaded, hopping district by district while the city streams in.
- Long trips across the whole map get more time before the teleport gives up (60 s instead of 30 s).

## 1.3.0 (2026-10-28)

New
- Xbox controller support. Hold BACK (View) for half a second to open or close the menu; a quick tap still opens the map. D-pad or left stick to move and change values (hold to repeat), A to toggle or run, LB / RB to switch tabs, B to close or cancel a confirmation.
- While the menu is open, the controller only drives the menu: Batman and the camera ignore it. START still pauses the game.
- The key hints at the bottom of the menu and in the confirmation box switch to controller buttons when you use the controller.
- SYSTEM > CONTROLLER: Controller Support on / off and Hold To Open time.
- New [gamepad] section in mod.toml: pick another open button or a combination such as "lb+rb", and the hold time. It is added to an existing mod.toml automatically.

## 1.2.0 (2026-10-28)

New
- CHARACTER tab: play as Nightwing, Robin, Catwoman, Batgirl, Red Hood or Harley Quinn in the open world. The screen slowly fades to black and you come back as the new character right where you stood. BACK TO BATMAN swaps back. Batgirl, Red Hood and Harley Quinn need their story pack DLC.
- Every character can drive the Batmobile: walk up to it and press E (or use CALL / ENTER BATMOBILE in the CHARACTER tab). Driving, battle mode and getting out work like they do for Batman.
- Save protection: saving pauses while you play as someone other than Batman, and loading the story always brings Batman back, even if you quit the game as another character.
- MUSIC tab: play your own songs in game. Drop .mp3, .wav or .wma files into BmGame\Mods\Oracle\Music. Play / pause, next, previous, stop, volume, in order / shuffle / repeat one, pause with the game's pause menu, reload the song list, open the folder.
- Rain (VISUAL tab > WEATHER): Vanilla, No Rain, Drizzle, Heavy or Storm, plus a Wet Streets slider. Works together with the sky & fog looks and post processing.
- On-screen confirmation box for character switches and for actions that change your save.

Improved
- Notices like NOW PLAYING and SAVING PAUSED also show while the menu is open.
- The tab bar shrinks to fit when there are many tabs.

## 1.1.0 (2026-10-28)

New
- NEW GAME ++ in the main menu: play New Game + again from the start once your save has a New Game + run. Switch it on in SYSTEM > MAIN MENU, then restart the game.
- Counter Icons in NG+ (COMBAT tab): brings back the counter warning icons above attacking enemies in New Game + and Knightmare.
- Menu colours (SYSTEM > MENU LOOK): Emerald, Cyan, Blue, Violet, Magenta, Red, Orange, Gold, White, or Rainbow, which cycles through every colour. Rainbow Speed slider.
- Low-End Mode: a lighter menu for slower PCs (the rainbow steps 15 times a second and the menu draws fewer effects).
- Teleport Fade (WORLD tab): QUICK (the old fade) or SLOW FADE, a gentle fade to black before the jump.

Fixed
- The main menu background video turned black with the mod installed. Cheats no longer switch on in the main menu, and post processing goes back to the game's own settings when you return to it.
- Cheats now follow whichever character you play (Catwoman, Nightwing, Robin, Harley Quinn, Red Hood, Batgirl and others), not only Batman.
- If the game stops sending updates to mods, the menu still opens and works.

Improved
- Menu draws faster: colours and text sizes are worked out once instead of every frame.
- Long help texts wrap onto a second line instead of running off the panel.
- Help texts say "you" instead of "Batman" where a cheat works for every character.

## 1.0.0 (2026-10-23)

First public release.

- Seven-tab cheat menu (F3): Player, Combat, Enemies, Visual, Batmobile, World, System.
- Teleport to Waypoint across islands, with a fade to black while the city loads.
- Sky & Fog looks with custom hue, strength, fog density and desaturation; works together with the post processing modes.
- Live Batmobile cheats: invincibility, infinite missile power, no cooldowns, perfect accuracy.
- Freeze Enemies holds enemies in place and stops their attacks.
- Hotkeys for any cheat, cheats remembered across loads, active cheats overlay.
