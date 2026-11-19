#!/usr/bin/env python3
"""Add observation-only logging to a disposable native project copy."""
import argparse
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]


def replace(path, needle, replacement):
    source = path.read_text()
    if source.count(needle) != 1:
        raise RuntimeError(f"expected one Fireball probe witness in {path}: {needle!r}")
    path.write_text(source.replace(needle, replacement, 1))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("project", type=Path)
    args = parser.parse_args()
    project = args.project.resolve()
    if project == ROOT or ROOT in project.parents:
        parser.error("pass a disposable project outside the source worktree")

    renderer = project / "game/client/EffectSystem.cs"
    authority = project / "src/VortexArena.Common/Gameplay/Weapons/Fireball.cs"
    for file in (renderer, authority):
        if ROOT in file.resolve().parents:
            parser.error("copy the source into the disposable project rather than linking it")
    replace(authority, "        if (ownerHealthAfter >= ownerHealthBefore && self.Cnt == 0\n",
            '''        Console.WriteLine($"FIREBALL_AUTH explode x={self.Origin.X:G9} y={self.Origin.Y:G9} z={self.Origin.Z:G9} before={ownerHealthBefore:G9} after={ownerHealthAfter:G9} cnt={self.Cnt:G9} bfgRadius={Primary.BfgRadius:G9}");
        if (ownerHealthAfter >= ownerHealthBefore && self.Cnt == 0
''')
    replace(renderer, "        var (model, material) = sphere;",
            '''        var (model, material) = sphere;
        GD.Print($"FIREBALL_MODEL receipt origin={effect.Origin} radius={effect.Radius:G9} model={model.GetType().Name} wall_ms={Time.GetTicksMsec()}");''')
    replace(renderer, "        _fireballBlasts.Add((model, material, finalScale, null));",
            '''        _fireballBlasts.Add((model, material, finalScale, null));
        GD.Print($"FIREBALL_MODEL material={material.GetType().Name} texture={(material.GetShaderParameter(PlayerSkinShader.AlbedoUniform).AsGodotObject() is Texture2D bound ? $"{bound.GetWidth()}x{bound.GetHeight()}" : "none")} extent={modelExtent:G9} final_scale={finalScale:G9} wall_ms={Time.GetTicksMsec()}");''')
    replace(renderer, "            if (!FireballBlastEffect.Sample(age, out float scaleFraction, out float alpha))",
            '''            bool visible = FireballBlastEffect.Sample(age, out float scaleFraction, out float alpha);
            GD.Print($"FIREBALL_MODEL sample age={age:G9} scale={scaleFraction * blast.FinalScale:G9} alpha={alpha:G9} lingering={visible} wall_ms={Time.GetTicksMsec()}");
            if (!visible)''')


if __name__ == "__main__":
    main()
