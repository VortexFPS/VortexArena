using System;
using Godot;
using VortexArena.Common.Framework;
using VortexArena.Game.Loaders.Models;

namespace VortexArena.Game.Client;

/// <summary>
/// Drives a skeletal DPM model's <see cref="AnimationPlayer"/> from a bound <see cref="Entity"/>'s networked
/// <see cref="Entity.Frame"/> — the DPM analog of <see cref="ModelAnimator.FollowEntityFrame"/> for MD3.
///
/// <para>DarkPlaces' <c>Mod_FrameGroupify</c> rewrites a skeletal model's scene list into the <c>.framegroups</c>
/// ranges, so a server that sets <c>self.frame = N</c> plays the Nth frame GROUP. The port's monster brain
/// (<c>MonsterAI.DriveAnimFrame</c> → <c>Spider.AnimFrame</c>) follows that exactly: it stamps <c>Entity.Frame</c>
/// with the group ORDINAL from the QC <c>mr_anim</c> map (spider: idle 5, walk/run 10, bite 0, web-shoot 3,
/// pain1/2 7/8, die1/2 1/2). <see cref="DpmBuilder"/> bakes those groups into <see cref="AnimationPlayer"/> clips
/// in framegroup order and records their names on the model root (<see cref="DpmBuilder.FrameGroupClipsMeta"/>).
/// This node reads that ordinal each frame and plays the matching clip, so the spider visibly walks/bites/webs/
/// pains/dies instead of holding its bind pose.</para>
///
/// <para>Without a bound entity (or before the model carries clip metadata) it is inert — the AnimationPlayer's
/// own autoplay/idle keeps running, so a non-frame-driven DPM prop is unaffected.</para>
/// </summary>
public partial class DpmFrameDriver : Node3D
{
    private AnimationPlayer? _player;
    private string[] _clipsByGroup = Array.Empty<string>();
    private int _lastGroup = int.MinValue;

    /// <summary>The entity whose networked <see cref="Entity.Frame"/> (frame-group ordinal) drives playback.</summary>
    public Entity? Entity { get; set; }

    /// <summary>
    /// Attach a driver for a freshly built DPM model root and bind it to <paramref name="entity"/>. Returns the
    /// driver (already parented to <paramref name="built"/>) when the model carries frame-group clip metadata and
    /// an <see cref="AnimationPlayer"/>; returns null otherwise (e.g. a single-clip / metadata-less DPM), so the
    /// caller can leave the model on its own autoplay. Mirrors how the MD3 path opts an entity into
    /// <see cref="ModelAnimator.FollowEntityFrame"/>.
    /// </summary>
    public static DpmFrameDriver? TryAttach(Node3D built, Entity entity)
    {
        if (built is null || entity is null)
            return null;
        if (!built.HasMeta(DpmBuilder.FrameGroupClipsMeta))
            return null;
        if (built.FindChild("AnimationPlayer", recursive: true, owned: false) is not AnimationPlayer player)
            return null;

        var meta = built.GetMeta(DpmBuilder.FrameGroupClipsMeta).AsGodotArray();
        var names = new string[meta.Count];
        for (int i = 0; i < meta.Count; i++)
            names[i] = meta[i].AsString();
        if (names.Length == 0)
            return null;

        var driver = new DpmFrameDriver
        {
            Name = "DpmFrameDriver",
            Entity = entity,
            _player = player,
            _clipsByGroup = names,
        };
        built.AddChild(driver);
        return driver;
    }

    public override void _Process(double delta)
    {
        if (_player is null || Entity is null || Entity.IsFreed || _clipsByGroup.Length == 0)
            return;

        // Entity.Frame is the frame-GROUP ordinal (Mod_FrameGroupify). Clamp into range; a server that networks
        // an out-of-table frame just holds the nearest valid group rather than throwing.
        int group = Math.Clamp((int)MathF.Floor(Entity.Frame), 0, _clipsByGroup.Length - 1);
        if (group == _lastGroup)
            return; // same group → let the clip keep playing (PlayIfChanged semantics; no restart per frame)
        _lastGroup = group;

        string clip = _clipsByGroup[group];
        // Clips live in the default ('') library, so the play name is just the clip name (no "lib/" prefix).
        if (!string.IsNullOrEmpty(clip) && _player.HasAnimation(clip))
            _player.Play(clip);
    }
}
