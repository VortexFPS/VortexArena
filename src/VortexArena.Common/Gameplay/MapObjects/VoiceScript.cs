// Port of qcsrc/common/mapobjects/target/voicescript.qc (target_voicescript) — SVQC half.
//
// target_voicescript plays a scripted sequence of voice lines to a player while they hold it as their active
// voice script. When triggered (.use) it latches onto the activator (actor.voicescript = this) and seeds the
// schedule; each frame, target_voicescript_next(pl) advances the script: it picks the next sound token, plays
// it to that player (play2), and schedules the following line after the line's duration + a randomized wait.
//
// The message token list is "<file> <dur> <file> <dur> ... * <rndfile> <rnddur> ...": the part before the '*'
// is the ORDERED prefix (cnt lines), the part after is a RANDOM pool the script loops through once exhausted.
// A NEGATIVE duration means "no delay after this line" (voiceend = time - dt; nextthink = voiceend).
//
// Port notes:
//  * tokenize_console + stof are small local helpers (the message is a plain whitespace-separated token list).
//  * play2(pl, sample) is the per-player audible play — reduced to the sound facade (Voice channel), the same
//    convention CompatRemaps/Doors use for play2. Sound PATHS are "<netname>/<file>.wav".
//  * game_stopped maps to VehicleCommon.GameStopped (the port's match-ended global).
//  * target_voicescript_next is the per-player tick; the host's per-client think calls VoiceScript.Next(pl)
//    (mirroring QC's call from the player frame). Exposed public so the server frame can drive it.

using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;
using System.Globalization;

namespace XonoticGodot.Common.Framework
{
    public partial class Entity
    {
        /// <summary>QC player <c>.voicescript</c> — the target_voicescript currently driving this player (NULL = none).</summary>
        public Entity? VoiceScript;
        /// <summary>QC player <c>.voicescript_index</c> — index of the next voice line to play.</summary>
        public float VoiceScriptIndex;
        /// <summary>QC player <c>.voicescript_nextthink</c> — time the next line may start.</summary>
        public float VoiceScriptNextThink;
        /// <summary>QC player <c>.voicescript_voiceend</c> — time the current line finishes.</summary>
        public float VoiceScriptVoiceEnd;
    }
}

namespace XonoticGodot.Common.Gameplay
{
    using XonoticGodot.Common.Framework;

    /// <summary><c>target_voicescript</c> — a scripted voice-line sequence. Registered by <see cref="MapObjectsRegistry"/>.</summary>
    public static class VoiceScript
    {
        /// <summary><c>spawnfunc(target_voicescript)</c> (voicescript.qc:89-115).</summary>
        public static void VoiceScriptSetup(Entity this_)
        {
            this_.ClassName = "target_voicescript";
            this_.Use = VoiceScriptUse;
            this_.Active = MapMover.ActiveActive;
            this_.Reset = VoiceScriptReset;

            // QC: n = tokenize_console(message); cnt = n/2; scan for the '*' split, precache each sound.
            string[] argv = Tokenize(this_.Message);
            int n = argv.Length;
            this_.Cnt = n / 2;
            for (int i = 0; i + 1 < n; i += 2)
            {
                if (argv[i] == "*")
                {
                    this_.Cnt = i / 2; // QC: this.cnt = i / 2; ++i;
                    ++i;
                }
                // QC: precache_sound(strcat(netname, "/", argv(i), ".wav")) — precache is implicit in the port.
            }

            MapMover.IndexRegister(this_);
        }

        /// <summary>Port of <c>target_voicescript_use</c> (voicescript.qc:13-23): latch this script onto the activator.</summary>
        private static void VoiceScriptUse(Entity this_, Entity actor)
        {
            if (this_.Active != MapMover.ActiveActive)
                return;
            if (!ReferenceEquals(actor.VoiceScript, this_))
            {
                actor.VoiceScript = this_;
                actor.VoiceScriptIndex = 0f;
                actor.VoiceScriptNextThink = MapMover.Now() + this_.Delay;
            }
        }

        /// <summary>Port of <c>target_voicescript_reset</c> (voicescript.qc:84-87): re-arm on round restart.</summary>
        private static void VoiceScriptReset(Entity this_)
        {
            this_.Active = MapMover.ActiveActive;
        }

        /// <summary>Port of <c>target_voicescript_clear</c> (voicescript.qc:8-11): drop a player's active script.</summary>
        public static void Clear(Entity pl)
        {
            pl.VoiceScript = null;
        }

        /// <summary>
        /// Port of <c>target_voicescript_next</c> (voicescript.qc:25-82): the per-player tick. Drive the active
        /// script — when the current line has finished and the next-think time has arrived, pick the next token
        /// (ordered prefix then the looping random pool), play it to the player, and schedule the line after.
        /// The host's per-client server frame calls this for each player carrying a script.
        /// </summary>
        public static void Next(Entity pl)
        {
            Entity? vs = pl.VoiceScript;
            if (vs is null)                                  // QC: if(!vs) return;
                return;
            if (vs.Active != MapMover.ActiveActive)          // QC: if(vs.active != ACTIVE_ACTIVE) { pl.voicescript = NULL; return; }
            {
                pl.VoiceScript = null;
                return;
            }
            if (string.IsNullOrEmpty(vs.Message))            // QC: if(vs.message == "") return;
                return;
            if ((pl.Flags & EntFlags.Client) == 0)           // QC: if (!IS_PLAYER(pl)) return;
                return;
            if (VehicleCommon.GameStopped)                   // QC: if(game_stopped) return;
                return;

            float now = MapMover.Now();
            if (now < pl.VoiceScriptVoiceEnd)                // QC: if(time >= pl.voicescript_voiceend) { ... }
                return;
            if (now < pl.VoiceScriptNextThink)               // QC: if(time >= pl.voicescript_nextthink) { ... }
                return;

            // QC: n = tokenize_console(vs.message);
            string[] argv = Tokenize(vs.Message);
            int n = argv.Length;
            int idx = (int)pl.VoiceScriptIndex;

            // pick the token index `i` (voicescript.qc:52-57)
            int i;
            if (idx < vs.Cnt)
                i = idx * 2;                                 // ordered prefix
            else if (n > vs.Cnt * 2)
                i = ((idx - vs.Cnt) % ((n - vs.Cnt * 2 - 1) / 2)) * 2 + vs.Cnt * 2 + 1; // looping random pool
            else
                i = -1;                                      // nothing left

            // QC branches on i >= 0; the `i + 1 < n` is a port-side bounds guard (QC would read garbage / crash
            // on a malformed message — on well-formed data the index math keeps i+1 < n, so it never diverges).
            if (i >= 0 && i + 1 < n)
            {
                // QC voicescript.qc:61: play2(pl, strcat(vs.netname, "/", argv(i), ".wav")) — a per-recipient 2D
                // cue (CH_INFO / VOL_BASE / ATTEN_NONE), not a positional Voice-channel emit.
                MapMover.Play2(pl, vs.NetName + "/" + argv[i] + ".wav");

                float dt = Stof(argv[i + 1]);
                if (dt >= 0f)
                {
                    // QC: voiceend = time + dt; nextthink = voiceend + vs.wait * (0.5 + random());
                    pl.VoiceScriptVoiceEnd = now + dt;
                    pl.VoiceScriptNextThink = pl.VoiceScriptVoiceEnd + vs.Wait * (0.5f + Prandom.Float());
                }
                else
                {
                    // QC: voiceend = time - dt; nextthink = voiceend; (negative dt = no extra delay)
                    pl.VoiceScriptVoiceEnd = now - dt;
                    pl.VoiceScriptNextThink = pl.VoiceScriptVoiceEnd;
                }

                ++pl.VoiceScriptIndex;                       // QC: ++pl.voicescript_index;
            }
            else
            {
                pl.VoiceScript = null;                       // QC: else pl.voicescript = NULL; // stop trying then
            }
        }

        /// <summary>QC <c>tokenize_console</c> reduced to a whitespace split (the message is a plain token list).</summary>
        private static string[] Tokenize(string s)
            => string.IsNullOrEmpty(s) ? System.Array.Empty<string>()
             : s.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);

        /// <summary>QC <c>stof</c>: parse a token as a float (0 on failure), invariant culture.</summary>
        private static float Stof(string s)
            => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;
    }
}
