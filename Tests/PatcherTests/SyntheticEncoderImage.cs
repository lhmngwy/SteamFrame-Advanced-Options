using System.Buffers.Binary;
using System.Text;

namespace SteamLinkVRSettings.Tests;

// A tiny, non-executable test PE assembled from independent instruction examples.
// No Valve binary or extracted function is distributed with the tests.
internal sealed class SyntheticEncoderImage
{
    public byte[] Bytes { get; } = new byte[0x6000];
    public int Query { get; private set; }
    public int Hook { get; private set; }
    public int DefaultLevel { get; private set; }
    public int LevelStore { get; private set; }
    public int PresetVersion { get; private set; }
    public int InitVersion { get; private set; }
    public int ConfigPointerStore { get; private set; }
    public int LevelEnd { get; private set; }
    public int PresetEnd { get; private set; }
    public int Chain { get; private set; }
    private readonly List<(int Begin, int End, int Unwind)> functions = [];
    private int cursor;

    public SyntheticEncoderImage(int stack = -0x10, int config = 0x928, int table = 0x9a60, int pointer = 0x270, bool useRdi = false, bool useEcx = false, bool chain = true, bool scheduled = false)
    {
        Write16(0, 0x5a4d); Write32(0x3c, 0x80); Write32(0x80, 0x4550);
        Write16(0x84, 0x8664); Write16(0x86, 3); Write16(0x94, 240);
        Write16(0x98, 0x20b); Write32(0x98 + 56, 0x6000); Write32(0x98 + 60, 0x400); Write32(0x98 + 108, 16);
        Write32(0x98 + 120, 0x3000); Write32(0x98 + 124, 40);
        Section(0x188, 0x1000, 0x800, 0x1000, 0x60000020);
        Section(0x1b0, 0x3000, 0x2000, 0x2000, 0x40000040);
        Section(0x1d8, 0x5000, 0x1000, 0x1000, 0x40000040);
        Write32(0x3000, 0x3200); Write32(0x300c, 0x3100); Write32(0x3010, 0x3220);
        Encoding.ASCII.GetBytes("VCRUNTIME140.dll\0").CopyTo(Bytes, 0x3100);
        Encoding.ASCII.GetBytes("memcpy\0").CopyTo(Bytes, 0x3122);
        Write32(0x3200, 0x3120); Write32(0x3220, 0x3120);
        Write32(0x3300, 0x43680000); Write32(0x3304, 0x43af0000);
        Convert.FromHexString("88dc0c7922457b4d9425bda9975f7603").CopyTo(Bytes, 0x3400);

        cursor = 0x1000;
        Emit("F3 44 0F 10 15 00 00 00 00 F3 0F 11 8F 40 01 00 00 49 8B 8E 88 01 00 00 48 8B 01 FF 90 88 00 00 00 84 C0 74 09 F3 44 0F 10 15 00 00 00 00 C3");
        Relative(0x1005, 0x1009, 0x3304); Relative(0x102a, 0x102e, 0x3300);
        AddFunction(0x1000, cursor);
        string obj(int modrm) => (modrm + (useRdi ? 1 : 0)).ToString("X2");

        cursor = 0x1100;
        Emit("31 D2 C7 85"); InitVersion = cursor; Int32(0x560); Emit("0B 00 02 71 41 B8 F4 09 00 00 48 8D 8D"); Int32(0x564); Call();
        Emit($"48 8D {obj(0x9e)}"); Int32(table);
        Emit("41 B8 F8 09 00 00 48 8B CB 48 8D 95"); Int32(0x560); Call(); Emit("48 8B CB FF D0");
        int split = cursor;
        Emit($"48 8D {obj(0x9e)}"); Int32(config);
        Emit("C7 85"); Int32(0x560); Emit("0B 00 07 F1 31 D2");
        ConfigPointerStore = cursor;
        Emit($"48 89 {obj(0x9e)}"); Int32(pointer);
        Emit("41 B8 FC 0D 00 00 48 8D 8D"); Int32(0x564); Call();
        Emit("48 8B CB 48 8D 95"); Int32(0x560); Emit("41 B8 00 0E 00 00"); Call(); Emit("90 90 C3");
        if (chain)
        {
            AddFunction(0x1100, split);
            AddFunction(split, cursor, parent: functions.Count - 1);
            Chain = functions[^1].Unwind;
        }
        else AddFunction(0x1100, cursor);

        cursor = 0x1300;
        PresetVersion = cursor;
        Emit("C7 85"); Int32(stack); Emit("0B 00 04 F1 C7 85"); Int32(stack + 8); Emit("0B 00 07 F1");
        Emit("48 8D 85"); Int32(stack); Emit("48 89 44 24 20");
        Query = cursor; Emit($"FF {obj(0x96)}"); Int32(table + 0x140);
        Emit($"48 8B {obj(0x8e)}"); Int32(pointer); Emit("48 8D 95"); Int32(stack + 8); Emit("41 B8 00 0E 00 00");
        Hook = cursor; Call(); Emit("C3"); PresetEnd = cursor; AddFunction(0x1300, cursor);

        cursor = 0x1400;
        Emit($"48 8B {obj(0x86)}"); Int32(0x21c); Emit("48 3B 05"); int lo = cursor; Int32(0);
        Emit("0F 85"); int noLo = cursor; Int32(0);
        Emit($"48 8B {obj(0x86)}"); Int32(0x224); Emit("48 3B 05"); int hi = cursor; Int32(0);
        Emit("0F 85"); int noHi = cursor; Int32(0);
        Emit("41 BA BA 00 00 00");
        Emit($"C7 {obj(0x86)}"); Int32(config + 0xac); Int32(1);
        Emit(useEcx ? "B9" : "B8"); DefaultLevel = cursor; Int32(183);
        Emit("48 81 FA 00 00 80 7F"); Emit(useEcx ? "41 0F 4F CA" : "41 0F 4F C2");
        if (scheduled) { Emit($"8B {obj(0x96)}"); Int32(0x1b0); }
        LevelStore = cursor; Emit($"89 {obj(useEcx ? 0x8e : 0x86)}"); Int32(config + 0xa8);
        LevelEnd = cursor; Emit("C3"); AddFunction(0x1400, cursor);
        Relative(lo, lo + 4, 0x3400); Relative(hi, hi + 4, 0x3408);
        Relative(noLo, noLo + 4, LevelEnd); Relative(noHi, noHi + 4, LevelEnd);

        cursor = 0x1600; Emit("FF 25"); Relative(cursor, cursor + 4, 0x3220); cursor += 4;
        Write32(0x98 + 136, 0x5000); Write32(0x98 + 140, functions.Count * 12);
        for (int i = 0; i < functions.Count; i++)
        {
            Write32(0x5000 + i * 12, functions[i].Begin); Write32(0x5004 + i * 12, functions[i].End); Write32(0x5008 + i * 12, functions[i].Unwind);
        }
    }

    public void Write32(int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(Bytes.AsSpan(at), value);
    public void Relative(int at, int next, int target) => Write32(at, target - next);
    private void Write16(int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Bytes.AsSpan(at), value);
    private void Section(int at, int rva, int size, int rawSize, int flags)
    { Write32(at + 8, size); Write32(at + 12, rva); Write32(at + 16, rawSize); Write32(at + 20, rva); Write32(at + 36, flags); }
    private void Emit(string bytes) { var data = Convert.FromHexString(bytes.Replace(" ", "")); data.CopyTo(Bytes, cursor); cursor += data.Length; }
    private void Int32(int value) { Write32(cursor, value); cursor += 4; }
    private void Call() { Emit("E8"); Relative(cursor, cursor + 4, 0x1600); cursor += 4; }
    private void AddFunction(int begin, int end, int parent = -1)
    {
        int unwind = 0x5100 + functions.Count * 32;
        Bytes[unwind] = parent < 0 ? (byte)1 : (byte)0x21;
        if (parent >= 0)
        { Write32(unwind + 4, functions[parent].Begin); Write32(unwind + 8, functions[parent].End); Write32(unwind + 12, functions[parent].Unwind); }
        functions.Add((begin, end, unwind));
    }
}
