// Port of Base/darkplaces/cl_parse.c CL_ParseTempEntity (the non-QuakeWorld branch, lines 2565-2910)
// and CL_ParseBeam. Only the wire reads are ported; the particles, lights and sounds DarkPlaces
// spawns from them are the renderer's business.
using System.Numerics;

namespace VortexArena.Legacy.Protocol;

/// <summary>
/// Decodes the temp entities the engine defines itself. A game's client QuakeC may define more (and
/// Xonotic does, with ids of its own); those are not here and cannot be, which is why svc_temp_entity
/// is offered to the handler first.
/// </summary>
public static class DpTempEntityParser
{
    /// <summary>
    /// Read one temp entity starting at its type byte. Returns false if the type is not an engine
    /// one ("CL_ParseTempEntity: bad type" in DarkPlaces, a fatal error there); the reader is then
    /// one byte past where it started. A short read is reported through <see cref="DpMessageReader.BadRead"/>.
    /// </summary>
    public static bool TryParse(DpMessageReader r, out DpTempEntity te)
    {
        te = default;
        int type = r.ReadByte();
        if (type < 0)
            return false;
        te.Type = (TempEntityType)type;
        switch (te.Type)
        {
            // [vector] origin
            case TempEntityType.WizSpike:
            case TempEntityType.KnightSpike:
            case TempEntityType.Spike:
            case TempEntityType.SpikeQuad:
            case TempEntityType.SuperSpike:
            case TempEntityType.SuperSpikeQuad:
            case TempEntityType.PlasmaBurn:
            case TempEntityType.Gunshot:
            case TempEntityType.GunshotQuad:
            case TempEntityType.Explosion:
            case TempEntityType.ExplosionQuad:
            case TempEntityType.TarExplosion:
            case TempEntityType.SmallFlash:
            case TempEntityType.LavaSplash:
            case TempEntityType.Teleport:
            case TempEntityType.TeiBigExplosion:
                te.Origin = r.ReadVector();
                return true;

            // [vector] origin [char3] velocity [byte] count
            case TempEntityType.Blood:
            case TempEntityType.Spark:
            {
                te.Origin = r.ReadVector();
                float x = r.ReadChar(), y = r.ReadChar(), z = r.ReadChar();
                te.Direction = new Vector3(x, y, z);
                te.Count = r.ReadByte();
                return true;
            }

            // [vector] min [vector] max [coord] explosionspeed [short] count
            case TempEntityType.BloodShower:
                te.Origin = r.ReadVector();
                te.Origin2 = r.ReadVector();
                te.Speed = r.ReadCoord();
                te.Count = r.ReadUShort();
                return true;

            // [vector] min [vector] max [vector] dir [short] count [byte] color [byte] gravity [coord] randomvel
            case TempEntityType.ParticleCube:
                te.Origin = r.ReadVector();
                te.Origin2 = r.ReadVector();
                te.Direction = r.ReadVector();
                te.Count = r.ReadUShort();
                te.ColorStart = r.ReadByte();
                te.ColorLength = r.ReadByte();
                te.Speed = r.ReadCoord();
                return true;

            // [vector] min [vector] max [vector] dir [short] count [byte] color
            case TempEntityType.ParticleRain:
            case TempEntityType.ParticleSnow:
                te.Origin = r.ReadVector();
                te.Origin2 = r.ReadVector();
                te.Direction = r.ReadVector();
                te.Count = r.ReadUShort();
                te.ColorStart = r.ReadByte();
                return true;

            // [vector] origin [coord] red [coord] green [coord] blue
            case TempEntityType.Explosion3:
            {
                te.Origin = r.ReadVector();
                float red = r.ReadCoord() * 2.0f, green = r.ReadCoord() * 2.0f, blue = r.ReadCoord() * 2.0f;
                te.Color = new Vector3(red, green, blue);
                return true;
            }

            // [vector] origin [byte] red [byte] green [byte] blue
            case TempEntityType.ExplosionRgb:
                te.Origin = r.ReadVector();
                te.Color = ReadColorBytes(r);
                return true;

            // [vector] origin [byte] radius / 8 - 1 [byte] lifetime / 256 - 1 [byte] red [byte] green [byte] blue
            case TempEntityType.CustomFlash:
                te.Origin = r.ReadVector();
                te.Radius = (r.ReadByte() + 1) * 8;
                te.Speed = (float)((r.ReadByte() + 1) * (1.0 / 256.0));
                te.Color = ReadColorBytes(r);
                return true;

            // [vector] origin [vector] velocity [byte] count
            case TempEntityType.FlameJet:
            case TempEntityType.TeiSmoke:
            case TempEntityType.TeiPlasmaHit:
                te.Origin = r.ReadVector();
                te.Direction = r.ReadVector();
                te.Count = r.ReadByte();
                return true;

            // [entity] entity [vector] start [vector] end
            case TempEntityType.Lightning1:
            case TempEntityType.Lightning2:
            case TempEntityType.Lightning3:
            case TempEntityType.Beam:
                ReadBeam(r, ref te);
                return true;

            // [string] model [entity] entity [vector] start [vector] end
            case TempEntityType.Lightning4Neh:
                te.Model = r.ReadString();
                ReadBeam(r, ref te);
                return true;

            // [vector] origin [byte] startcolor [byte] colorcount
            case TempEntityType.Explosion2:
                te.Origin = r.ReadVector();
                te.ColorStart = r.ReadByte();
                te.ColorLength = r.ReadByte();
                return true;

            // [vector] start [vector] end [vector] angles
            case TempEntityType.TeiG3:
                te.Origin = r.ReadVector();
                te.Origin2 = r.ReadVector();
                te.Direction = r.ReadVector();
                return true;

            default:
                return false;
        }
    }

    // CL_ParseBeam. An entity number at or above MAX_EDICTS is replaced by 0 ("no owner"), as there.
    private static void ReadBeam(DpMessageReader r, ref DpTempEntity te)
    {
        int entity = r.ReadUShort();
        te.Origin = r.ReadVector();
        te.Origin2 = r.ReadVector();
        te.Entity = entity >= DpProtocol.MaxEdicts ? 0 : entity;
    }

    private static Vector3 ReadColorBytes(DpMessageReader r)
    {
        float red = r.ReadByte() * (2.0f / 255.0f), green = r.ReadByte() * (2.0f / 255.0f), blue = r.ReadByte() * (2.0f / 255.0f);
        return new Vector3(red, green, blue);
    }
}
