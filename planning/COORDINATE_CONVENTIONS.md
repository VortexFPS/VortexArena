# Coordinate & angle conventions — Quake/Xonotic ↔ Godot

**Read this before touching anything that converts positions, directions, basis vectors, view angles, or
warpzone/portal transforms between the simulation (Quake) and the renderer (Godot).** Mixing the two
conventions silently produces mirrored / rotated / inverted-pitch results that *look* plausible but are wrong —
the warpzone portal-camera and teleport-exit angle bugs both live in this seam.

## The two spaces

| | Quake / Xonotic (the SIM) | Godot (the RENDERER) |
|---|---|---|
| Up axis | **+Z** | **+Y** |
| Handedness | right-handed | right-handed |
| Forward (a camera/entity) | **+X** at yaw 0 | local **−Z** (cameras look down −Z) |
| Right | derived by `AngleVectors` | local **+X** |
| Up | derived by `AngleVectors` | local **+Y** |
| Position type | `System.Numerics.Vector3` | `Godot.Vector3` |
| Angles | Euler **(pitch=X, yaw=Y, roll=Z)** degrees | Euler, different order; **do not** hand Quake angles to a Godot `Basis`/`Quaternion` Euler ctor |

All simulation math (`src/XonoticGodot.Common`, `.Server`, `.Engine`) is **Quake**. The render layer
(`game/`) converts at the boundary. `WarpzoneTransform`, `QMath`, physics, traces, entity origins/angles are all
Quake.

## The axis swap — `XonoticGodot.Game.Coords` (`game/Coords.cs`)

```csharp
public static Godot.Vector3 ToGodot(System.Numerics.Vector3 q) => new(q.X,  q.Z, -q.Y);
public static System.Numerics.Vector3 ToQuake(Godot.Vector3 g)  => new(g.X, -g.Z,  g.Y);
```

Properties (verified):
- It is a **proper rotation** (determinant **+1**) — it is a −90° rotation about X, **not** a mirror. So it never
  introduces a handedness flip; a mirrored result means the bug is elsewhere (the transform, not the swap).
- It is an exact inverse: `ToQuake(ToGodot(q)) == q` and `ToGodot(ToQuake(g)) == g`.
- It is **linear with no translation**, so it applies identically to **positions and to direction/basis vectors**
  (you may convert a normal or a basis column with the same call).

## The pitch trap — `QMath` (`src/XonoticGodot.Common/Math/QMath.cs`)

Quake pitch is **DOWN-positive**: `AngleVectors` computes `forward.Z = -sin(pitch)`, so a *positive* pitch aims
*down*. This is the opposite of the "up-positive" pitch most engines (and `atan2(z, horiz)`) produce. Consequences:

- `AngleVectors(angles) → forward/right/up` — the authoritative Quake angles→vectors. Use this, then
  `Coords.ToGodot`, to orient a Godot node from Quake angles. **Never** build a Godot Euler basis from raw Quake
  angles — the order and the pitch sign both differ.
- `VecToAngles(dir)` returns **up-positive** pitch (non-inverting). `VecToAngles(AngleVectors(a).forward).X == -a.pitch`.
- `FixedVecToAngles(dir)` returns **down-positive** pitch — it is the proper inverse of `AngleVectors` and is what
  QC's `fixedvectoangles` macro uses. **Use `FixedVecToAngles`, not `VecToAngles`, whenever you round-trip a
  direction back to angles that will be fed to `AngleVectors`/`makevectors`** (e.g. warpzone `TransformAngles`,
  any "aim a thing along a vector" code). Using `VecToAngles` there inverts the pitch.

## Building a Godot camera/node basis from Quake (the proven pattern)

From `game/client/FirstPersonView.cs` (the one first-person path — copy it, don't reinvent):

```csharp
QMath.AngleVectors(viewAnglesQuake, out NVec3 fq, out NVec3 rq, out NVec3 uq);
camera.GlobalBasis = new Basis(Coords.ToGodot(rq), Coords.ToGodot(uq), -Coords.ToGodot(fq));
camera.GlobalPosition = Coords.ToGodot(originQuake);
```

- Godot `new Basis(x, y, z)` takes the basis **COLUMNS** (the node's local axes in parent space). A camera looks
  down its **local −Z**, so the **Z column = −forward**. Hence `(right, up, −forward)`.
- `Basis.X / .Y / .Z` read those columns back. So to recover the Quake basis from a live Godot camera:
  ```csharp
  NVec3 fwdQ   = Coords.ToQuake(-cam.GlobalBasis.Z); // local −Z is forward
  NVec3 rightQ = Coords.ToQuake( cam.GlobalBasis.X);
  NVec3 upQ    = Coords.ToQuake( cam.GlobalBasis.Y);
  ```
  This is the exact inverse of the construction above (proven round-trip). Prefer this **vector** path over
  extracting Euler angles — it sidesteps the pitch-sign and Euler-order traps entirely.

## Entity render: one basis, DarkPlaces' entity matrix (fixed 2026-10-31, playtest-bugs #76)

Every entity node is oriented by **`Coords.EntityBasis(angles, model)`** (`game/Coords.cs`), which takes the three
Quake vectors from `VortexArena.Common.Math.EntityRenderAxes` and uses `ToGodot` of them as the basis columns
`(forward, up, right)`. That is DarkPlaces' `Matrix4x4_CreateFromQuakeEntity`, including DP's **alias-model
pitch reversal** (`cl_main.c` CL_UpdateNetworkEntity / `CL_GetPitchSign`: MDL/MD2/MD3/ZYM/DPM/PSK/IQM draw with
`-pitch`; brush, OBJ and sprite models do not). QC is written against that reversal — `spawnfunc(misc_gamemodel)`
flips `angles_x` so the draw-time flip lands on the mapper's value — so a port that flips in the spawnfunc but not
at draw time tilts props the wrong way. A pure yaw θ comes out as **+θ about Godot +Y**
(`EntityRenderAxesTests` pins all of this against a transcription of the DP matrix and against real map entities).

**History — the trap this replaced.** `EntityNode` used to have a second, yaw-only path,
`Rotation = (0, −DegToRad(yaw), 0)`. Godot's +Y rotation carries +X toward −Z, which is Quake +Y, so a Quake yaw θ
is +θ in Godot and the shortcut was the **mirror image** (checked headless: `Rotation.Y = −90°` sends +X to
`(0, 0, +1)` = Quake −Y). Every item a mapper turned to 90° drew at 270° (#76), and the carried CTF flag, placed
with Quake-space `AngleVectors` math but oriented through the shortcut, counter-rotated (#7). #7 had been worked
around by pre-negating the flag's networked yaw in `Ctf.cs`; that negation was removed with the central fix. The
same −yaw Euler lived in `ClientWorld`'s vehicle driver and both `ViewEntityRenderer`s; all now use
`Coords.EntityBasis` / `Coords.YawBasis`.

**Rule:** orient a Godot node from Quake angles only through `Coords.EntityBasis` (or `YawBasis`), never through a
Godot Euler, and keep an attached entity's **position and orientation on the same convention**.

## Warpzone / portal transforms

`WarpzoneTransform` (`src/XonoticGodot.Common/Gameplay/MapObjects/Warpzone.cs`) is **entirely Quake**:
- `TransformOrigin(p)` warps a point IN-plane → OUT-plane; `Rotate(v)` warps a direction (the 180°-flipped
  basis change `inFwd→−outFwd, inRight→−outRight, inUp→+outUp` — a proper rotation, det +1).
- `TransformAngles(a) = FixedVecToAngles(Rotate(AngleVectors(a).forward))` — note the **`FixedVecToAngles`** (the
  pitch-correct inverse). Roll is dropped.
- The teleport (server) and the portal camera (`PortalRenderer`) use the **same** `WarpzoneTransform`, so a wrong
  exit-angle and a wrong portal view share a cause — look at (a) the `WarpzoneTransform` construction inputs
  (`InAngles`/`OutAngles` from `DerivePlaneFromBrush`'s surface normal — without a `target_position` aiment the
  normal's *sign* is just the brush face normal and is not re-oriented), then (b) the Quake↔Godot conversion in
  the renderer (proven correct), in that order. The `WarpzoneTransform` math itself is covered by
  `WarpzonePortalTests` (handedness `Rotate(inFwd) == −outFwd`, origin/inverse round-trips).

## Checklist when you touch angles/coords across the boundary
1. Is the value Quake or Godot? (Sim = Quake; `game/` render = Godot.)
2. Converting a **direction/normal/basis vector**? `Coords.ToGodot/ToQuake` works directly (linear).
3. Round-tripping a **direction → angles → vectors**? Use **`FixedVecToAngles`**, never `VecToAngles`.
4. Orienting a **Godot node**? Build the basis from `AngleVectors` + `Coords` columns `(right, up, −forward)` —
   never feed Quake Euler angles to a Godot Euler ctor.
5. A **mirrored** result ⇒ a handedness flip you introduced (the swap doesn't); an **inverted pitch** ⇒ a
   `VecToAngles`/`FixedVecToAngles` mix-up; a **180°** result ⇒ a forward/normal sign or an in/out swap.
6. Orienting an **entity node** from angles? Use `Coords.EntityBasis` — a Godot Euler `Rotation.Y = −yaw` is handedness-flipped
   vs. the full-basis path — if the entity's position is Quake-space math, orient it with the full-basis path too,
   or the two counter-rotate (the carried-flag trap above).
