# Changelog

## 1.2.0 (2026-10-28)

New
- Gamepad (Xbox / XInput) support. On the gadget wheel, highlight the REC gun / guns slot: a bar under the wheel shows Y to switch between the REC gun and the guns and X to keep the pistols in his hands. Hold LT to aim, RT to shoot, LT + X to reload (it replaces the game's LT + X quickfire while the guns are aimed). Banners show controller buttons when you're playing on a controller. Buttons and right stick aim speed are set in the new [gamepad] section of BatmanGuns.ini, which is added to existing settings files automatically.

Fixed
- Quickfire now spends a round per shot (6 per quickfire), so the ammo pips on the wheel and in the corner go down.
- The clip refills even while another gadget is selected; before, it stayed part-empty until the guns were held again.
- No more batarang reticle in the middle of the screen during a guns quickfire.

## 1.1.0 (2026-10-28)

New
- The guns are now a secondary gadget: they share the REC gun's slot on the gadget wheel (key 4) instead of replacing the Batmobile Remote. Tap 4 for the REC gun as usual, hold 4 for one second to switch the slot to the guns (and back). An on-screen banner confirms the switch, and the choice is remembered between sessions. The Batmobile Remote stays on its own key and works as normal.
- Dead zone aiming: while aiming, the reticle moves freely inside a zone and the camera follows it on a spring. Shots always go exactly where the reticle is.
- Recoil: aimed shots kick the aim up and sideways and jolt the camera.
- Aim speed: the mouse is slowed while aiming the guns (adjustable).
- 12-round clip (was 6) with an ammo counter beside the reticle while aiming: the round count, /12, and a row of bullets that empties as you fire. It turns red when you are low.
- Manual reload: press R while the guns are in hand. The counter shows RELOADING with a progress bar. If R is bound to another gadget in your keybinds, it only reloads while the guns are in hand.
- F5 keeps the pistols in Batman's hands at all times, with any gadget and in combat. Press F5 again to put them back in the holsters.
- Both hands now close around the pistols when they're held outside the aim pose.
- Settings file BatmanGuns.ini in the mod folder (dead zone size and spring, aim speed, recoil, switch hold time). Saved changes apply right away.

Changed
- The thigh holsters are always shown when the pistols aren't in his hands, whatever gadget is selected.
- The old free aim is replaced by the dead zone aiming.
- The automatic reloads are unchanged: an empty clip reloads itself while aiming, and the clip refills a couple of seconds after you lower the guns.

Fixed
- Pistols could stay stuck in Batman's hands after he was knocked out and the game reloaded the checkpoint. Leftover pistol models are now removed.

## 1.0.0

First public release.

- Red Hood's dual handguns in the Batmobile Remote's slot on Batman's gadget wheel, with Red Hood's icon and an ammo counter on the wheel and HUD.
- Close waist-up aim camera with the Disruptor reticle and free aim; each click fires one of Red Hood's at-range beatdown strikes with muzzle flashes.
- Double-tap the key to quickfire.
- Every shot is lethal: shot thugs never get back up, can't be revived by medics, and Detective Mode shows them as DECEASED.
- Batman grips both pistols with Red Hood's gun animations and carries them in thigh holsters.
