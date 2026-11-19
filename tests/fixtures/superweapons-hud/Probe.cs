using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Godot;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;
using VortexArena.Formats.Vfs;
using VortexArena.Game.Client;
using VortexArena.Game.Hud;
using VortexArena.Game.Menu;
using VortexArena.Legacy.Csqc;
using VortexArena.QuakeC;
using VortexArena.Server;
using Vector3 = System.Numerics.Vector3;

public partial class Probe : Node
{
    private const string ColorCvar = "hud_progressbar_superweapons_color";
    private static readonly Color SkinTint = new(0.25f, 0.5f, 0.75f);
    private const ItemFlag SuperweaponBits = ItemFlag.Superweapon | ItemFlag.UnlimitedSuperweapons;
    private static readonly MethodInfo BuildRows = typeof(PowerupsPanel).GetMethod(
        "BuildRows", BindingFlags.Instance | BindingFlags.NonPublic)!;

    public override void _Ready()
    {
        try
        {
            PowerupsPanel.RegisterDefaults(MenuState.Cvars);
            MenuState.Cvars.Set(ColorCvar, "0.25 0.5 0.75");
            using var pin = new PinnedPanel(ProjectSettings.GlobalizePath("res://pin/csprogs.dat"));
            var panel = new PowerupsPanel { Visible = false };
            AddChild(panel);

            CheckSpawn("g_nix", "NIX spawn", panel, pin);
            CheckSpawn("g_instagib", "InstaGib spawn", panel, pin);
            CheckPickup(panel, pin);
            CheckMissingStoreCountdown();
            CheckPoweroff();
            CheckPoweroffCleanup();
            CheckPickupChannelStop();

            GD.Print("Superweapons HUD probe passed");
            GetTree().Quit();
        }
        catch (Exception ex)
        {
            GD.PrintErr(ex);
            GetTree().Quit(1);
        }
    }

    private static GameWorld Boot(string mutator = "")
    {
        var world = new GameWorld(new CollisionWorld(),
            [new EntityDict("info_player_deathmatch", new Vector3(128, 0, 0))]);
        world.Boot("dm", applyStartupOverrides: store =>
        {
            store.Set("g_balance_superweapons_time", "24");
            store.Set("g_spawnshieldtime", "0");
            if (mutator.Length > 0)
                store.Set(mutator, "1");
        });
        return world;
    }

    private static Player Spawn(GameWorld world)
    {
        var player = new Player { PlayerId = 1, NetName = "HUD probe" };
        Check(world.Clients.Spawn(player), "player spawn");
        return player;
    }

    private static void CheckSpawn(string mutator, string label, PowerupsPanel panel, PinnedPanel pin)
    {
        var world = Boot(mutator);
        try
        {
            Player player = Spawn(world);
            bool instagib = mutator == "g_instagib";
            if (instagib)
                Check(player.OwnedWeapons.Contains("vaporizer"), "InstaGib spawn grants Vaporizer");
            CheckRows(label, panel, pin, player, world.Time, ItemFlag.UnlimitedSuperweapons, null,
                statusActive: instagib);
            PlayerFrameLogic.PlayerPowerups(player);
            CheckRows(label + " after powerup pass", panel, pin, player, world.Time, ItemFlag.UnlimitedSuperweapons,
                null, statusActive: instagib);
        }
        finally
        {
            world.Shutdown();
            MutatorActivation.DeactivateAll();
        }
    }

    private static void CheckPickup(PowerupsPanel panel, PinnedPanel pin)
    {
        var world = Boot();
        try
        {
            Player player = Spawn(world);
            var item = Api.Entities.Spawn();
            Check(SpawnFuncs.TrySpawn("weapon_fireball", item), "Fireball spawnfunc");
            Check(!item.ItemAvailable && item.SuperweaponsFinished == 24f, "Fireball schedules 24s pickup");
            world.Simulation.Tick();
            ItemPickupRules.Respawn(item);
            item.Touch!(item, player);
            Check(player.OwnedWeapons.Contains("fireball"), "Fireball touch grants weapon");
            float now = Api.Clock.Time;
            CheckRows("status before ordinary bit", panel, pin, player, now, 0, null, statusActive: true);

            PlayerFrameLogic.PlayerPowerups(player);
            CheckRows("timed pickup", panel, pin, player, now, ItemFlag.Superweapon, 24, statusActive: true);
            CheckRows("timed countdown", panel, pin, player, now + 8, ItemFlag.Superweapon, 16, statusActive: true);

            // A valid timer can overlap the unlimited bit, including before its persistent flag is synced.
            player.Items |= (int)ItemFlag.UnlimitedSuperweapons;
            CheckRows("both bits with live timer", panel, pin, player, now + 8, SuperweaponBits, null, statusActive: true);
            StatusEffectsCatalog.Tick(player, world.Time);
            Check(player.StatusEffects.Any(s => s.DefId == StatusEffectsCatalog.Superweapon!.RegistryId
                && (s.Flags & StatusEffectFlags.Persistent) != 0), "unlimited effect became persistent");
            CheckRows("both bits with persistent status", panel, pin, player, now + 8, SuperweaponBits, null, statusActive: true);

            player.Items &= ~(int)ItemFlag.UnlimitedSuperweapons;
            StatusEffectsCatalog.Tick(player, world.Time);
            CheckRows("timed countdown resumes", panel, pin, player, now + 10, ItemFlag.Superweapon, 14, statusActive: true);

            while (world.Time <= now + 24.1f)
                world.Simulation.Tick();
            PlayerFrameLogic.PlayerPowerups(player);
            StatusEffectsCatalog.Tick(player, world.Time);
            Check(!player.OwnedWeapons.Contains("fireball"), "timed Fireball expires");
            CheckRows("expired pickup", panel, pin, player, world.Time, 0, null);

            player.Items |= (int)(ItemFlag.Jetpack | ItemFlag.FuelRegen);
            CheckRows("unrelated permanent rows", panel, pin, player, world.Time, 0, null);
            var rows = NativeRows(panel);
            Check(rows.Count == 2 && rows.All(row => row.Infinite)
                && rows.Select(row => row.Icon).SequenceEqual(new[] { "jetpack", "fuelregen" }),
                "jetpack and fuel regeneration keep infinity rows");
        }
        finally
        {
            world.Shutdown();
            MutatorActivation.DeactivateAll();
        }
    }

    private static List<PowerupsPanel.PowerupEntry> NativeRows(PowerupsPanel panel)
        => (List<PowerupsPanel.PowerupEntry>)BuildRows.Invoke(panel, null)!;

    private static void CheckMissingStoreCountdown()
    {
        var world = Boot();
        try
        {
            const float step = 1f / 16f;
            world.Simulation.TickSeconds = step;
            var player = Spawn(world);
            var cues = new List<SoundEvent>();
            world.Services.SoundImpl.Broadcast += sound =>
            {
                if (ReferenceEquals(sound.Source, player) && sound.Sample == "misc/poweroff")
                    cues.Add(sound);
            };
            foreach (var (time, expectedCues) in new[] { (1f, 1), (1f + 2 * step, 0) })
            {
                while (Api.Clock.Time < time)
                    world.Simulation.Tick();
                Check(Api.Clock.Time == time && Api.Clock.FrameTime == step, "exact no-store probe clock");
                player.Items |= (int)ItemFlag.UnlimitedSuperweapons;
                player.OwnedWeapons.Add("fireball");
                PlayerFrameLogic.PlayerPowerups(player);
                Check(!player.HasStatusEffectStore && (player.Items & (int)SuperweaponBits) == (int)SuperweaponBits
                    && cues.Count == 0, "no-store unlimited holder arms both bits silently");
                player.Items &= ~(int)ItemFlag.UnlimitedSuperweapons;

                // Like a flag change between status and player thinking, no status tick intervenes here.
                PlayerFrameLogic.PlayerPowerups(player);
                Check(cues.Count == expectedCues && cues.All(cue => cue.Channel == SoundChannel.Auto),
                    $"no-store countdown at {time}: expected {expectedCues} cue(s)");
                Check((player.Items & (int)SuperweaponBits) == 0 && !player.OwnedWeapons.Contains("fireball"),
                    "no-store cleanup strips ordinary bit and weapon");
                PlayerFrameLogic.PlayerPowerups(player);
                Check(cues.Count == expectedCues, "no-store countdown cannot repeat after cleanup");
                cues.Clear();
            }
        }
        finally
        {
            world.Shutdown();
            MutatorActivation.DeactivateAll();
        }
    }

    private static void CheckPoweroff()
    {
        var world = Boot();
        try
        {
            var player = Spawn(world);
            var status = StatusEffectsCatalog.Superweapon!;
            var events = new List<SoundEvent>();
            world.Services.SoundImpl.Broadcast += sound =>
            {
                if (ReferenceEquals(sound.Source, player) && (sound.Sample == "misc/poweroff" || sound.Stop))
                    events.Add(sound);
            };
            world.Simulation.Tick();

            void CheckCue(SoundChannel channel)
            {
                Check(events.Count == 1, $"one {channel} poweroff");
                var sound = events[0];
                Check(ReferenceEquals(sound.Source, player) && sound.Channel == channel
                    && sound.Volume == SoundLevels.VolBase && sound.Attenuation == SoundLevels.AttenNorm,
                    $"{channel} poweroff source and mix");
                events.Clear();
            }

            player.Items |= (int)ItemFlag.Superweapon;
            player.OwnedWeapons.Add("fireball");
            StatusEffectsCatalog.Apply(player, status, 1);
            player.IsBot = true;
            PlayerFrameLogic.PlayerPowerups(player);
            Check(events.Count == 0, "bot countdown silent");
            player.IsBot = false;

            player.Items |= (int)ItemFlag.UnlimitedSuperweapons;
            StatusEffectsCatalog.Tick(player, world.Time);
            PlayerFrameLogic.PlayerPowerups(player);
            Check(events.Count == 0, "overlapping unlimited countdown silent");
            player.OwnedWeapons.Remove("fireball");
            PlayerFrameLogic.PlayerPowerups(player);
            Check(events.Count == 0 && !StatusEffectsCatalog.Has(player, status)
                && (player.Items & (int)SuperweaponBits) == (int)ItemFlag.UnlimitedSuperweapons,
                "persistent loss silent, ordinary bit cleared");

            player.OwnedWeapons.Add("fireball");
            PlayerFrameLogic.PlayerPowerups(player);
            Check(!StatusEffectsCatalog.Has(player, status), "unlimited bit never invents a timer");
            player.Items &= ~(int)SuperweaponBits;
            StatusEffectsCatalog.Apply(player, status, 10);
            player.OwnedWeapons.Remove("fireball");
            PlayerFrameLogic.PlayerPowerups(player);
            Check(events.Count == 0 && !StatusEffectsCatalog.Has(player, status), "orphan status cleared silently");

            player.OwnedWeapons.Add("fireball");
            StatusEffectsCatalog.Apply(player, status, 1);
            PlayerFrameLogic.PlayerPowerups(player);
            PlayerFrameLogic.PlayerPowerups(player);
            CheckCue(SoundChannel.Auto);

            player.OwnedWeapons.Remove("fireball");
            PlayerFrameLogic.PlayerPowerups(player);
            CheckCue(SoundChannel.TriggerAuto);
            Check((player.Items & (int)ItemFlag.Superweapon) == 0, "normal loss clears ordinary bit");

            player.OwnedWeapons.Add("fireball");
            StatusEffectsCatalog.Apply(player, status, Api.Clock.FrameTime);
            PlayerFrameLogic.PlayerPowerups(player);
            world.Simulation.Tick();
            StatusEffectsCatalog.Tick(player, Api.Clock.Time);
            PlayerFrameLogic.PlayerPowerups(player);
            CheckCue(SoundChannel.Auto);
            Check(!player.OwnedWeapons.Contains("fireball"), "exact timeout strips weapon");
            world.Simulation.Tick();
            StatusEffectsCatalog.Tick(player, Api.Clock.Time);
            PlayerFrameLogic.PlayerPowerups(player);
            Check(events.Count == 0 && !StatusEffectsCatalog.Has(player, status), "status timeout adds no cue");

            player.OwnedWeapons.Add("fireball");
            StatusEffectsCatalog.Apply(player, status, Api.Clock.FrameTime);
            world.Simulation.Tick();
            PlayerFrameLogic.PlayerPowerups(player);
            Check(events.Count == 0 && !player.OwnedWeapons.Contains("fireball")
                && !StatusEffectsCatalog.Has(player, status), "unannounced expired status removed silently");

            player.Items |= (int)SuperweaponBits;
            player.OwnedWeapons.Add("fireball");
            StatusEffectsCatalog.Apply(player, status, Api.Clock.FrameTime);
            float deadline = StatusEffectsCatalog.GetTime(player, status, Api.Clock.Time);
            StatusEffectsCatalog.Tick(player, Api.Clock.Time);
            world.Simulation.Tick();
            world.Simulation.Tick();
            StatusEffectsCatalog.Tick(player, Api.Clock.Time);
            PlayerFrameLogic.PlayerPowerups(player);
            Check(events.Count == 0 && (player.Items & (int)SuperweaponBits) == (int)SuperweaponBits
                && player.OwnedWeapons.Contains("fireball")
                && StatusEffectsCatalog.GetTime(player, status, Api.Clock.Time) == deadline,
                "overlap holds the weapon and keeps the original elapsed deadline");
            player.Items &= ~(int)ItemFlag.UnlimitedSuperweapons;
            StatusEffectsCatalog.Tick(player, Api.Clock.Time);
            PlayerFrameLogic.PlayerPowerups(player);
            CheckCue(SoundChannel.Auto);
            Check((player.Items & (int)SuperweaponBits) == 0
                && !player.OwnedWeapons.Contains("fireball") && !StatusEffectsCatalog.Has(player, status),
                "clearing an elapsed overlap expires the weapon with the final countdown cue");

            player.Items |= (int)SuperweaponBits;
            PlayerFrameLogic.PlayerPowerupsRemoveAll(player, allowPoweroffSound: false);
            Check((player.Items & (int)SuperweaponBits) == 0 && events.Count == 1
                && events[0].Stop && events[0].Channel == SoundChannel.Item,
                "silent bit cleanup stops only the single-channel pickup cue");
            GD.Print("Superweapons poweroff probe passed");
        }
        finally
        {
            world.Shutdown();
            MutatorActivation.DeactivateAll();
        }
    }

    private static void CheckPoweroffCleanup()
    {
        var world = Boot();
        try
        {
            while (Api.Clock.Time <= world.GameStartTime + 1f)
                world.Simulation.Tick();

            var player = world.Clients.ClientConnect(netName: "Cleanup probe").Player;
            Check(world.Clients.Spawn(player), "cleanup player spawn");
            var sounds = new List<SoundEvent>();
            world.Services.SoundImpl.Broadcast += sound =>
            {
                if (ReferenceEquals(sound.Source, player) && (sound.Sample == "misc/poweroff" || sound.Stop))
                    sounds.Add(sound);
            };

            player.Items |= (int)SuperweaponBits;
            player.DeadState = DeadFlag.Dying;
            PlayerFrameLogic.PlayerPowerups(player);
            Check(sounds.Select(sound => (sound.Channel, sound.Stop)).SequenceEqual(
                new[] { (SoundChannel.Auto, false), (SoundChannel.Item, true) }), "death cue and pickup stop");
            Check((player.Items & (int)SuperweaponBits) == 0, "death clears powerup bits");
            Check(world.Clients.Spawn(player), "respawn after death");

            sounds.Clear();
            player.Items |= (int)SuperweaponBits;
            world.Clients.PutObserverInServer(player);
            Check(sounds.Select(sound => (sound.Channel, sound.Stop)).SequenceEqual(
                new[] { (SoundChannel.Auto, false), (SoundChannel.Item, true) }), "observer transition cue and pickup stop");
            Check((player.Items & (int)SuperweaponBits) == 0, "observer transition clears powerup bits");
            Check(world.Clients.Spawn(player), "respawn from observer");

            sounds.Clear();
            player.Items |= (int)SuperweaponBits;
            StatusEffectsCatalog.Apply(player, StatusEffectsCatalog.Superweapon!, 10f);
            world.RestartMatch();
            Check(sounds.Select(sound => (sound.Channel, sound.Stop)).SequenceEqual(
                new[] { (SoundChannel.Item, true), (SoundChannel.TriggerAuto, false) }),
                "restart suppresses item-bit cue and preserves normal status removal");
            Check((player.Items & (int)SuperweaponBits) == 0, "restart clears powerup bits");

            // Set a start time in the past to distinguish the FL_CLIENT gate from the match-start gate.
            world.GameStartTime = Api.Clock.Time - 2f;
            sounds.Clear();
            player.Items |= (int)SuperweaponBits;
            StatusEffectsCatalog.Apply(player, StatusEffectsCatalog.Superweapon!, 10f);
            Check(world.Clients.ClientDisconnect(player), "live client disconnect");
            Check(sounds.Select(sound => (sound.Channel, sound.Stop)).SequenceEqual(
                new[] { (SoundChannel.TriggerAuto, false), (SoundChannel.Item, true) }),
                "disconnect plays normal status removal and stops the pickup without an item-bit cue");
            Check((player.Items & (int)SuperweaponBits) == 0, "disconnect clears powerup bits");
        }
        finally
        {
            world.Shutdown();
            MutatorActivation.DeactivateAll();
        }
    }

    private void CheckPickupChannelStop()
    {
        using var stream = new AudioStreamWav { MixRate = 8000, Data = new byte[8000] };
        var client = new ClientWorld { AudioLoader = _ => stream };
        AddChild(client);
        try
        {
            int channel = (int)SoundChannel.Item;
            client.OnSound("misc/powerup", Vector3.Zero, channel: channel, sourceNetId: 1);
            var voice = client.GetChildren().OfType<AudioStreamPlayer3D>().Single();
            Check(voice.Playing, "pickup single-channel voice started");
            client.OnStopSound(1, channel);
            Check(!voice.Playing, "pickup single-channel voice stopped");

            client.OnSound("misc/powerup", Vector3.Zero, channel: channel, sourceNetId: 1);
            voice.Stop(); // End the one-shot and let the pool reuse its voice.
            client.OnSound("misc/poweroff", Vector3.Zero, sourceNetId: 2);
            client.OnStopSound(1, channel);
            Check(voice.Playing, "late pickup stop leaves the reused voice playing");
            voice.Stop();
        }
        finally
        {
            client.Free();
        }
    }

    private static void CheckRows(string label, PowerupsPanel panel, PinnedPanel pin, Player player,
        float now, ItemFlag expectedBits, float? expectedSeconds, bool statusActive = false)
    {
        ItemFlag items = (ItemFlag)player.Items;
        Check((items & SuperweaponBits) == expectedBits, label + " item bits");
        var effects = player.StatusEffects.Where(s => s.DefId == StatusEffectsCatalog.Superweapon!.RegistryId).ToArray();
        Check(effects.Length == (statusActive ? 1 : 0), label + " native status count");
        panel.Player = player;
        panel.Now = now;
        var native = NativeRows(panel).Where(row => row.Icon == "superweapons").ToArray();
        var stock = pin.Rows((int)items, now, effects.SingleOrDefault());
        Check(native.Length == stock.Length && native.Length == (expectedSeconds.HasValue ? 1 : 0),
            label + " native/pinned row count");
        if (expectedSeconds is float seconds)
        {
            foreach (var row in new[] { native.Single(), stock.Single() })
                Check(row.Icon == "superweapons" && !row.Infinite && row.Lifetime == 30f
                    && MathF.Abs(row.TimeLeft - seconds) < 0.001f && row.Color == SkinTint,
                    $"{label} countdown row: icon={row.Icon}, left={row.TimeLeft}, life={row.Lifetime}, tint={row.Color}, infinite={row.Infinite}");
        }
        GD.Print($"{label} | item bits 0x{(int)expectedBits:x} | native and pinned {(expectedSeconds is float t ? $"{t}s" : "hidden")}");
    }

    private sealed class PinnedPanel : IDisposable
    {
        private readonly VirtualFileSystem _files = new();
        private readonly QcCoreBuiltins _builtins;
        private readonly QcVm _vm;
        private readonly int _actor;
        private readonly int _row;
        private int _items;

        public PinnedPanel(string csprogs)
        {
            _vm = new QcVm(ProgsFile.Load(File.ReadAllBytes(csprogs)), "pinned CSQC", initialEdicts: 8);
            _actor = _vm.AllocEdict();
            _row = _vm.AllocEdict();
            int effect = _vm.AllocEdict();
            _vm.GlobalInt(Global("powerupItems")) = _row;
            _vm.GlobalInt(Global("StatusEffects_first")) = effect;
            _vm.GlobalInt(Global("g_statuseffects")) = _actor;
            _builtins = new QcCoreBuiltins(_vm, new LegacyQcHost(MenuState.Cvars, _files));
            _builtins.Register();
            // Install the one relevant registry entry and seed its fields with the pinned constructor.
            _vm.FieldInt(effect, Field("classname")) = _vm.EngineString("Superweapon");
            _vm.SetArgInt(0, effect);
            _vm.Execute(Function("spawnSuperweapon_1"), 1);
            _vm.FieldInt(effect, Field("m_active")) = Function("StatusEffect_m_active");
            _vm.FieldInt(effect, Field("m_tick")) = Function("Superweapon_m_tick");
            _vm.GlobalVector(Global("autocvar_" + ColorCvar)) = new QcVector(SkinTint.R, SkinTint.G, SkinTint.B);
            _vm.RegisterBuiltin(331, vm =>
            {
                Check(vm.ArgFloat(0) == 15 && vm.ArgFloat(1) == 0 && vm.ArgFloat(2) == 24,
                    "pinned STAT_ITEMS lookup");
                vm.ReturnFloat(_items);
            });
        }

        public PowerupsPanel.PowerupEntry[] Rows(int items, float now, ActiveStatusEffect status)
        {
            _items = items;
            _vm.GlobalFloat(Global("time")) = now;
            _vm.FieldFloat(_actor, ArrayFirstField("statuseffect_time")) = status.ExpireTime;
            _vm.FieldFloat(_actor, ArrayFirstField("statuseffect_flags")) = (int)status.Flags;
            _vm.Execute(Function("resetPowerupItems"), 0);
            _vm.SetArgInt(0, _actor);
            _vm.Execute(Function("StatusEffects_tick"), 1);
            float count = _vm.GlobalFloat(Global("powerupItemsCount"));
            if (count == 0f)
                return [];

            Check(count == 1f, "pinned row count");
            QcVector tint = _vm.FieldVector(_row, Field("colormod"));
            return [new PowerupsPanel.PowerupEntry(
                _vm.GetString(_vm.FieldInt(_row, Field("message"))),
                _vm.GetString(_vm.FieldInt(_row, Field("netname"))),
                _vm.FieldFloat(_row, Field("count")), _vm.FieldFloat(_row, Field("lifetime")),
                new Color(tint.X, tint.Y, tint.Z), _vm.FieldFloat(_row, Field("cnt")) != 0f)];
        }

        public void Dispose()
        {
            _builtins.Dispose();
            _files.Dispose();
        }

        private int Global(string name) => (_vm.FindGlobal(name) ?? throw new Exception("Missing global " + name)).Offset;
        private int Field(string name) => (_vm.FindField(name) ?? throw new Exception("Missing field " + name)).Offset;
        private int ArrayFirstField(string name) => _vm.FindField(name)?.Offset ?? Field(name + "[0]");
        private int Function(string name)
        {
            int function = _vm.FindFunction(name);
            Check(function != 0, "missing QC function " + name);
            return function;
        }
    }

    private static void Check(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException(description);
    }
}
