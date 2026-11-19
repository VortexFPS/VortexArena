# S1 row 17 Electro rubble limit

Pinned `common/effects/qc/rubble.qc` records each listed launch's `creationtime`, filters by classname and owner, and repeatedly forces the oldest entry to detonate until the count is at or below the limit. `ReplaceOldListedChildRubble` moves list membership and launch time to the replacement entity when an Electro orb sticks. The re-armed sticky timer does not participate in oldest selection. Only launches made under a positive limit join the list. The shipped limit and stick defaults are both zero.

The paired run used pin `863cd3e8456e18f577f7fe5fef857ff55ae62cc0`, the supplied stock SDL executable, and byte-identical compiled QC. Alpha fired using the normal secondary button and Beta joined as a second owner and observing client. The stock script used the existing Stormkeep entity overlay pattern. The native Godot Mono fixture used two negotiated ENet clients, the same launch placements and the normal secondary fire gate on a controlled floor. Static stick was enabled, with speed 1, radius 64, a long flight lifetime and sticky lifetimes of 75 then 35 seconds. The orb positions differ slightly by engine and muzzle placement. In each engine the overlap and isolation checks compare positions within that engine.

| Trial | Stock and native result |
| --- | --- |
| Launch at limit 0 | The orb remains unlisted when the cap is later enabled. |
| Alpha and Beta place orbs with cap 2 | The first listed Alpha orb sticks and has a later deadline than Alpha's second listed orb. Two Alpha orbs and one Beta orb are tracked. Neither owner triggers a detonation at this point. |
| Alpha fires over the oldest orb, cap 2 and projectile damage mode 0 | The oldest stuck Alpha orb detonates. The overlapping new orb survives, alongside the second listed Alpha orb, Beta's orb and the unlisted launch. Alpha and Beta each receive one impact. |
| Alpha fires over the next oldest orb, cap lowered to 1 and projectile damage mode 2 | Two older listed Alpha orbs detonate through the cap. The overlapping new orb then detonates through ordinary projectile damage. The unlisted launch and Beta's orb remain. Both clients receive three impacts. |

Server edict dumps record stock owner, origin, move type, launch time and deadline. The stock server's `W_Electro_Explode` stack contains the rubble helper for each forced blast and the regular delayed-use path for collateral. Both stock CSQC clients hit `Electro_wr_impacteffect` once and then three times in the corresponding trials. The native recipients decoded the same sequence of effect names and origins within their own engine. This is authority and recipient evidence. It does not compare rendered blast shape, playback audio, or moving-target follow and disconnect behavior. [Verified snapshots and impacts](observations.txt)

The pre-fix native test showed two impacts in mode 0. The ordinary blast incorrectly qualified for the projectile-damage exception and killed the overlapping new orb. Native now grants that exception only for the `electro_bolt` and `electro_orb_chain` inflictors used by stock. Cap removal uses the detonation callback independently of scheduled think. Timed detonation carries the stock splash tag for an airborne orb. A stuck orb clears the flying touch handler and preserves the native projectile identity and launch order. This uses the native protocol boundary under ADR-0005, ADR-0011 and ADR-0019. [Before-fix native trace](before-fix.txt)

## Reproduce

Use disposable profiles with the pinned data checkout and the project's existing restored native dependencies. The stock runner uses `STOCK_DIR` and `XONOTIC_PIN` overrides and defaults to `/tmp/va-reviewed-stock` and `/tmp/vortex-parity-base`. The stock compiled hashes were `c4a5a9e85598333568183779c06446b2d03f056d2da71636b6f86150ed1202f7` for `progs.dat` and `cbc9fd9a4e2c7fc8f02f4aab6e5883e549790eb24e71be2bc0b8a0b0fea89999` for `csprogs.dat`.

```sh
fixture=planning/parity/rubble-limits/electro-limit
ELECTRO_PROBE_PORT=29861 xvfb-run -a -s '-screen 0 1600x900x24' bash "$fixture/reproduce-stock.sh"
export GODOT=/tmp/vortex-parity-godot/Godot_v4.6.3-stable_mono_linux_x86_64/Godot_v4.6.3-stable_mono_linux.x86_64
export DOTNET_ROLL_FORWARD=Major
ELECTRO_PROBE_PORT=29862 bash "$fixture/reproduce-native.sh"
python3 "$fixture/summarize.py" /path/to/stock /path/to/native/probe.log
dotnet test tests/VortexArena.Tests/VortexArena.Tests.csproj --no-restore \
  --filter 'FullyQualifiedName~OrderedAuditGameplayTests|FullyQualifiedName~WeaponBalanceDefaultsTests'
```

## Moving attachment and follow loss

Alpha's secondary orb collided with the live Beta player and attached. Beta then moved in the same Stormkeep lane. Stock used the normal teleport command and the native two-client fixture set the same pose on its controlled floor. Stock used the normal kill command with its delay set to zero. Native applied the actual player death path. The move proved that the orb was following the target before the death trial.

In both engines the orb became a collidable toss on Beta's death, settled near the corpse and retained Alpha as owner, its original launch age and the sticky deadline. After Beta respawned, Alpha's next shot at cap one forced the dropped orb to detonate. Both clients received the one impact, and the newer orb attached to Beta. The previous native build kept the orb non-solid in Follow on death and retained a freed carrier after disconnect.

Stock showed two disconnect outcomes in otherwise identical runs. One removed the orb with no impact. The other still had a nonzero edict reference at the loss check and tossed the orb near world origin, leaving it listed. The pinned helper distinguishes a null attachment from a live edict whose classname or death state changed. A removed native entity has no live attachment identity. Native consistently takes the silent removal branch on disconnect, matching the stock clean outcome and dropping the stale-edict artifact. A subsequent native launch proved the cap slot was available without another blast. Class and death-state changes on an existing entity still drop the orb without changing owner or deadline. [Paired snapshots and repeated stock outcome](follow-observations.txt) · [Before-fix trace](follow-before.txt)

The paired moving target was a live player. This pass does not compare scripted moving brush rotation, frame-exact attachment angles, or blast audio. The scoped test also covers clearing the attachment reference and removing a carrier directly.

```sh
ELECTRO_PROBE_PORT=29866 xvfb-run -a -s '-screen 0 1600x900x24' bash "$fixture/reproduce-follow-stock.sh"
ELECTRO_PROBE_PORT=29869 ELECTRO_PROBE_STAGE=/path/to/disposable/earlier-electro-stage \
  bash "$fixture/reproduce-follow-native.sh"
python3 "$fixture/summarize-follow.py" /path/to/stock /path/to/native/profile/probe.log
```

Omit `ELECTRO_PROBE_STAGE` for a fresh build. Reuse copies only the Electro implementation and the fixture into the previously built disposable stage and creates a new profile with independent logs.

### Controlled input comparison

The original native fixture fired from the fallback muzzle at `1216 260 19` and left Beta's input yaw at 0 throughout. The stock readback put its launch at `1216 306 27.03125` and showed Beta changing yaw from −90 to 90 on teleport. The 46-unit muzzle difference also gave the original native shot more time to fall before contact. Both engines used player bounds of `-16 -16 -24` to `16 16 45`, eye offset `0 0 35`, and gravity 800. Native's baseline tick was 1/72 s and stock used 1/64 s.

For the control, stock fixed the horizontal and vertical muzzle adjustment with `g_shootfromfixedorigin "0 0 0"`, which retains the stock weapon's forward component. The native disposable probe registered the measured 58-unit forward offset, passed Beta's yaw through actual input, and used the stock tick. Both shots hit Beta through the live projectile hull trace. Native attached at yaw 270, equivalent to stock −90, with the same player-hull limit of −12 on the initial Y offset. Turning Beta to yaw 90 moved the orb to Y 460 in both engines. Native Z was 26.958984 and stock Z was 26.9902344. This difference matches the 1/32-unit difference already present at launch and in the player origins. The native test uses a controlled floor and samples post-death falling at a different time. Full map trace and frame-exact trajectory parity were outside this control. It identified no additional Electro follow gameplay mismatch. Native's general dedicated fallback muzzle and graphical model-tag registration were outside the row 17 comparison.

The native probe now reads the actual saved expiry from the follow callback before death and requires exact equality on the dropped orb. The controlled orb retained order 1 and expiry 80.156235. Stock retained its original creation time 30.8407249 and expiry 105.840729, and set `nextthink` to that expiry after the drop. Those are local simulation clocks. Native entered Toss on the first sampled 1/64 s step after the death. Stock was already in Toss at the next edict snapshot, so an exact stock death-to-drop tick was not measured. In both previously recorded stock disconnect outcomes and the control, the still-connected Alpha client recorded zero additional Electro impact callbacks after Beta's disconnect notification. Beta's useful impact evidence ends at disconnect. [Controlled inputs, clocks, and impact counts](follow-input-control.txt)

```sh
ELECTRO_INPUT_CONTROL=1 ELECTRO_PROBE_PORT=29872 xvfb-run -a -s '-screen 0 1600x900x24' \
  bash "$fixture/reproduce-follow-stock.sh"
ELECTRO_INPUT_CONTROL=1 ELECTRO_PROBE_PORT=29874 ELECTRO_PROBE_STAGE=/path/to/disposable/earlier-electro-stage \
  bash "$fixture/reproduce-follow-native.sh"
```
