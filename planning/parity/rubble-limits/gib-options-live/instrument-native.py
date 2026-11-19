from pathlib import Path


def insert(path, anchor, code):
    path = Path(path)
    text = path.read_text()
    if text.count(anchor) != 1:
        raise SystemExit(f"Probe anchor changed in {path}: {anchor!r}")
    path.write_text(text.replace(anchor, code + anchor))


insert("game/client/ModelGibs.cs", "        if (!(amount > 0f) || !float.IsFinite(amount))",
       '''        if (VortexArena.Game.Net.GibOptionsProbe.Recording)
            GD.Print($"GIB_SPLASH amount={amount} species={species}");
''')
insert("game/client/ModelGibs.cs", "        _pool[slot] = new Gib",
       '''        if (VortexArena.Game.Net.GibOptionsProbe.Recording)
            GD.Print($"GIB_MODEL {modelPath} skin={skin}");
''')
insert("game/client/EffectSystem.cs", "            float gentleGibs = cvars.GetFloat(\"cl_gentle_gibs\");",
       '''            if (VortexArena.Game.Net.GibOptionsProbe.Recording)
                GD.Print($"GIB_EVENT {gibKind} species={species} count={count}");
''')
insert("game/client/EffectSystem.cs", "            // A hit already carries the byte",
       '''            if (VortexArena.Game.Net.GibOptionsProbe.Recording)
                GD.Print($"GIB_REPLACEMENT {effectName} count={count}");
''')
insert("game/client/particles/ParticleRouter.cs", "        EffectStyleEntry style = Registry?.GetStyle(effectName) ?? EffectStyleEntry.Default;",
       '''        if (VortexArena.Game.Net.GibOptionsProbe.Recording
            && (effectName.Contains("dissolve") || effectName.Contains("damage_hit") || effectName.Contains("blood")))
            GD.Print($"GIB_PARTICLE {effectName} count={count} blocks={blocks.Count}");
''')
insert("game/client/particles/ParticleRouter.cs", "            Modern.Spawn(blocks, origin, velocity, count, preset, collisionEnabled: coverage);",
       '''            if (VortexArena.Game.Net.GibOptionsProbe.Recording
                && (effectName.Contains("dissolve") || effectName.Contains("damage_hit") || effectName.Contains("blood")))
                GD.Print($"MODERN_PARTICLE {effectName} count={count}");
''')
