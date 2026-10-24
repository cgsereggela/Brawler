using System;
using System.IO;
using System.Numerics;

namespace Brawler.Logic;

/// <summary>
/// One fighter in a host snapshot (GameManager.Sync, 60 Hz to every client): where they are, how they move, and what
/// the HUD shows. Packed to 26 bytes: HP to whole points (rounded up, so a sliver of HP still reads as alive), reload
/// to 1/255, block cooldown to 1/20 s, an active block to 1/100 s, the aim angle to a thousandth of a radian.
/// </summary>
public readonly record struct AvatarSnap(int Slot, bool Alive, bool Reloading, Vector2 Pos, Vector2 Vel, float Hp, int Ammo,
    float ReloadFrac, float BlockCd, float BlockActive, float Aim)
{
    public const int Size = 26;

    public void Write(BinaryWriter w)
    {
        w.Write((byte)Slot);
        w.Write((byte)((Alive ? 1 : 0) | (Reloading ? 2 : 0)));
        w.Write(Pos.X); w.Write(Pos.Y);
        w.Write(Vel.X); w.Write(Vel.Y);
        w.Write(I16((int)MathF.Ceiling(Hp)));
        w.Write((byte)Math.Clamp(Ammo, 0, 255));
        w.Write((byte)(int)(ReloadFrac * 255));
        w.Write((byte)Math.Clamp((int)(BlockCd * 20), 0, 255));
        w.Write((byte)Math.Clamp((int)(BlockActive * 100), 0, 255));
        w.Write(I16((int)(Aim * 1000)));
    }

    public static AvatarSnap Read(BinaryReader r)
    {
        int slot = r.ReadByte(), flags = r.ReadByte();
        var pos = new Vector2(r.ReadSingle(), r.ReadSingle());
        var vel = new Vector2(r.ReadSingle(), r.ReadSingle());
        float hp = r.ReadInt16();
        int ammo = r.ReadByte();
        float reload = r.ReadByte() / 255f, blockCd = r.ReadByte() / 20f, blockActive = r.ReadByte() / 100f;
        float aim = r.ReadInt16() / 1000f;
        return new AvatarSnap(slot, (flags & 1) != 0, (flags & 2) != 0, pos, vel, hp, ammo, reload, blockCd, blockActive, aim);
    }

    private static short I16(int v) => (short)Math.Clamp(v, short.MinValue, short.MaxValue);
}
