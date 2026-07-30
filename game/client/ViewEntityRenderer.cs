using System.Collections.Generic;
using Godot;
using VortexArena.Formats.Md3;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Net;

namespace VortexArena.Game.Client;

/// <summary>
/// Renders networked <b>weapon entities</b> — primarily the held weapon of a remote player (the third-person
/// counterpart of the local first-person <see cref="ViewModel"/>), and the Godot successor to CSQC's
/// weapon-entity attachment (lib/csqcmodel <c>CSQCModel_AutoTagIndex_Apply</c> /
/// <c>setattachment(wepent, player, "tag_weapon")</c>).
///
/// Two cases, both fed through <see cref="Update"/> by the entity bridge:
/// <list type="bullet">
///   <item><b>Player held weapon</b> — the server networks each player's active weapon as the <c>Weapon</c>
///         field on their CSQCModel state (<see cref="Entity.ActiveWeaponId"/>). The weapon's world model
///         (<c>v_*.md3</c>) is hung off the owner's <c>tag_weapon</c> hand tag so everyone sees what each
///         player carries. Switching weapons rebuilds the model; holstering (id &lt; 0) removes it.</item>
///   <item><b>Standalone view-model entity</b> — a <see cref="NetEntityKind.ViewModel"/> entity that isn't a
///         player's held weapon (a dropped weapon, a vehicle gun). Attached to its <see cref="NetEntityState.Owner"/>
///         tag when that owner is rendered, else it follows its own networked world pose.</item>
/// </list>
///
/// Attachment tracks the owner's <see cref="ModelAnimator"/> tag <see cref="Marker3D"/> the moment the owner's
/// model loads, so the weapon follows the hand for free. A player's weapon is hidden until their model is
/// rendered (the hand pose isn't known yet) rather than floated at the world origin.
/// </summary>
public sealed class ViewEntityRenderer
{
    private readonly ClientWorld _render;

    /// <summary>The owner model tag a held weapon attaches to (QC weapon attachment tag).</summary>
    public string WeaponTag { get; set; } = "tag_weapon";

    private sealed class Held
    {
        public required Node3D Node;       // the weapon model root
        public int AttachOwner = -1;       // whose hand tag to attach to (the player's net id)
        public bool PlayerHeld;            // true = a player's held weapon (hide until owner rendered)
        public int BuildKey = int.MinValue;// weaponId (player) or modelIndex (standalone) — rebuild on change
        public Node3D? AttachedTo;         // the owner tag we're parented to (null = unattached)
        public int TintKey = int.MinValue; // last owner colormap applied (#43) — re-tint only on change/rebuild
    }

    // Keyed by the networked entity's own net id (a player id, or a view-entity id — disjoint id spaces).
    private readonly Dictionary<int, Held> _held = new();

    public ViewEntityRenderer(ClientWorld render) => _render = render;

    /// <summary>
    /// Create/update the weapon visual for a networked entity. A <see cref="NetEntityKind.Player"/> drives the
    /// held weapon from <see cref="Entity.ActiveWeaponId"/>; any other kind is treated as a standalone weapon
    /// view-entity (model from the entity, attached to <see cref="NetEntityState.Owner"/> or following its pose).
    /// </summary>
    public void Update(Entity e, in NetEntityState s)
    {
        bool playerHeld = s.Kind == NetEntityKind.Player;
        int weaponId = e.ActiveWeaponId;

        // A player with no weapon (holstered / dead) carries nothing.
        if (playerHeld && weaponId < 0)
        {
            Remove(e.Index);
            return;
        }

        if (!_held.TryGetValue(e.Index, out Held? h))
        {
            h = new Held { Node = new Node3D { Name = $"wepent#{e.Index}" } };
            _render.RenderRoot.AddChild(h.Node);
            _held[e.Index] = h;
        }
        h.PlayerHeld = playerHeld;
        h.AttachOwner = playerHeld ? e.Index : s.Owner;

        int buildKey = playerHeld ? weaponId : s.ModelIndex;
        if (buildKey != h.BuildKey)
        {
            h.BuildKey = buildKey;
            if (playerHeld) RebuildFromWeapon(h, weaponId);
            else RebuildFromEntity(h, e);
            h.TintKey = int.MinValue; // fresh meshes start untinted — force the colormap re-apply below
        }

        EnsureAttached(h);

        // [r15 #43] the held gun wears the OWNER's colormap (Base weaponsystem.qc:180 `this.colormap =
        // this.owner.colormap`; glow = weaponentity_glowmod's pants-palette fallback): the FFA profile color,
        // the team color in teamplay, and any cl_forceplayercolors reassignment — PlayerColormap resolves all
        // three exactly like the body. Change-gated: the tree-walk only happens on a real color/model change.
        if (playerHeld)
        {
            int cm = _render.PlayerColormap(e);
            if (cm != h.TintKey)
            {
                h.TintKey = cm;
                ModelTint.ApplyColormap(h.Node, cm);
            }
        }

        // Unattached standalone entity: follow the networked world pose (the player case stays hidden — its
        // pose is the hand tag, which isn't available until the owner is rendered).
        if (h.AttachedTo is null && !h.PlayerHeld && GodotObject.IsInstanceValid(h.Node))
        {
            h.Node.Visible = true;
            h.Node.Position = Coords.ToGodot(e.Origin);
            h.Node.Rotation = new Vector3(0f, -Mathf.DegToRad(e.Angles.Y), 0f);
        }
    }

    /// <summary>Per-frame upkeep: re-check attachment for weapons whose owner model wasn't ready yet.</summary>
    public void Process()
    {
        foreach (Held h in _held.Values)
            if (h.AttachedTo is null || !GodotObject.IsInstanceValid(h.AttachedTo))
                EnsureAttached(h);
    }

    /// <summary>Drop a weapon view-entity (its owner left / holstered).</summary>
    public void Remove(int index)
    {
        if (_held.Remove(index, out Held? h) && GodotObject.IsInstanceValid(h.Node))
            h.Node.QueueFree();
    }

    /// <summary>Free every weapon view-entity (on teardown).</summary>
    public void Clear()
    {
        foreach (Held h in _held.Values)
            if (GodotObject.IsInstanceValid(h.Node))
                h.Node.QueueFree();
        _held.Clear();
    }

    // =====================================================================================
    //  Internals
    // =====================================================================================

    private void EnsureAttached(Held h)
    {
        if (!GodotObject.IsInstanceValid(h.Node))
            return;
        if (h.AttachedTo is not null && GodotObject.IsInstanceValid(h.AttachedTo) && h.Node.GetParent() == h.AttachedTo)
            return; // already attached to a still-valid hand tag

        Node3D? marker = h.AttachOwner >= 0 ? _render.GetAttachmentMarker(h.AttachOwner, WeaponTag) : null;
        if (marker is null || marker == _render.RenderRoot)
        {
            // Owner not rendered yet (no hand pose). Park under the render root; a player's weapon stays hidden
            // (don't float a gun at the world origin), a standalone weapon falls back to pose-following in Update.
            if (h.Node.GetParent() != _render.RenderRoot)
                h.Node.Reparent(_render.RenderRoot, keepGlobalTransform: false);
            if (h.PlayerHeld) h.Node.Visible = false;
            h.AttachedTo = null;
            return;
        }

        // Attach to the owner's hand tag and zero the local transform (the tag IS the hand pose).
        h.Node.Reparent(marker, keepGlobalTransform: false);
        h.Node.Transform = Transform3D.Identity;
        h.Node.Visible = true;
        h.AttachedTo = marker;
    }

    private void RebuildFromWeapon(Held h, int weaponId)
    {
        Md3Data? md3 = ResolveWeaponModel(weaponId, out string modelName);
        BuildInto(h, md3, modelName);
    }

    private void RebuildFromEntity(Held h, Entity e)
    {
        Md3Data? md3 = _render.ResolveModel(e);
        BuildInto(h, md3, e.Model);
    }

    private static void BuildInto(Held h, Md3Data? md3, string modelName)
    {
        foreach (Node c in h.Node.GetChildren())
            c.QueueFree();

        if (md3 is null)
        {
            // No model resolved: a small gun-shaped placeholder so the carried weapon is at least visible.
            h.Node.AddChild(new MeshInstance3D
            {
                Name = string.IsNullOrEmpty(modelName) ? "Placeholder" : modelName,
                Mesh = new BoxMesh { Size = new Vector3(6f, 6f, 26f) },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.2f, 0.2f, 0.22f) },
            });
            return;
        }

        // A held weapon plays its idle pose; a single static frame is enough (muzzle work is the owner's
        // first-person ViewModel). Build frame 0 and its tags (so muzzle effects could attach later).
        h.Node.AddChild(ModelLoader.BuildModel(md3, 0));
        if (md3.Tags.Count > 0)
            h.Node.AddChild(ModelLoader.BuildTags(md3, 0));
    }

    /// <summary>Resolve a weapon registry id to its world model (<c>v_*.md3</c>) via the host's resolver.</summary>
    private Md3Data? ResolveWeaponModel(int weaponId, out string modelName)
    {
        modelName = "";
        if (weaponId < 0 || weaponId >= Weapons.Count)
            return null;
        Weapon w = Registry<Weapon>.ById(weaponId);
        modelName = w.WorldModel ?? "";
        if (string.IsNullOrEmpty(modelName))
            return null;
        // Reuse ClientWorld's model resolver via a throwaway entity carrying the weapon's world-model name.
        return _render.ResolveModel(new Entity { ClassName = "weaponentity", Model = modelName, NetName = w.NetName });
    }
}
