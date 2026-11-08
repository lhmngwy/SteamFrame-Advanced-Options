using Iced.Intel;

namespace SteamLinkVRSettings.Frame;

public static partial class AdaptivePatchScanner
{
    // NVENC API 11.1 contracts, not Valve object/stack offsets. The API table's
    // GetEncodePresetConfigEx slot is 0x140; NV_ENC_CONFIG is 0xe00 bytes and its
    // HEVC level/tier fields are at 0xa8/0xac. Never infer new public ABI versions.
    private sealed record EncoderLayout(long Table, long ConfigPointer, long Config);

    private static (int Hook, int LevelImmediate) DiscoverInstructions(byte[] source, Image pe, int api)
    {
        var functions = pe.AnalysisFunctions();
        uint apiRva = pe.Rva(api);
        var initFunction = functions.Single(f => f.Begin <= apiRva && apiRva < f.End);
        var init = Decode(source, pe, initFunction);
        var imports = pe.MemcpyImports();
        var layout = DiscoverEncoderLayout(source, pe, init, pe.Rva(api), imports);
        var copies = new List<(int Hook, Function Function)>();
        var levels = new List<int>();

        // Seed candidate functions cheaply with public structure versions. All
        // matches are then decoded from their declared function entry: bytes in an
        // immediate, padding or the middle of an instruction cannot be accepted.
        foreach (var function in functions)
        {
            int start = pe.Offset(function.Begin), length = checked((int)(function.End - function.Begin));
            if (source.AsSpan(start, length).IndexOf(Convert.FromHexString("0b0004f1")) >= 0)
            {
                var code = Decode(source, pe, function, candidate: true);
                for (int i = 0; i + 1 < code.Count; i++)
                {
                    var version = code[i]; var configVersion = code[i + 1];
                    if (!StoreImmediate(version, 0xf104000b) || !StoreImmediate(configVersion, 0xf107000b) ||
                        !StackMemory(version) || !SameAddress(configVersion, version.MemoryBase, Displacement(version) + 8)) continue;
                    for (int q = i + 2; q + 4 < Math.Min(code.Count, i + 34); q++)
                    {
                        var query = code[q]; var destination = code[q + 1]; var from = code[q + 2]; var size = code[q + 3]; var copy = code[q + 4];
                        if (query.Mnemonic != Mnemonic.Call || query.Op0Kind != OpKind.Memory ||
                            !LoadMember(destination, Register.RCX) || query.MemoryBase != destination.MemoryBase ||
                            !Nonvolatile(query.MemoryBase) ||
                            !SameAddress(query, destination.MemoryBase, layout.Table + 0x140) ||
                            Displacement(destination) != layout.ConfigPointer ||
                            !Lea(from, Register.RDX, configVersion.MemoryBase, Displacement(configVersion)) ||
                            !MoveImmediate(size, Register.R8D, 0xe00) || !Memcpy(pe, source, copy, imports)) continue;
                        // The fifth Win64 query argument must point to the same
                        // NV_ENC_PRESET_CONFIG whose embedded config is copied.
                        if (q == 0 || !StoreRegister(code[q - 1], Register.RSP, 0x20) || code[q - 1].MemorySize != MemorySize.UInt64 ||
                            !AddressDefinition(code, q - 1, code[q - 1].Op1Register, version.MemoryBase, Displacement(version), i + 2) ||
                            code.Skip(i + 2).Take(q - i - 2).Any(IsBranch) || !SingleEntry(code, i, q + 4) ||
                            code.Skip(i + 2).Take(q + 2 - i).Any(x => x.FlowControl == FlowControl.Next &&
                                (Writes(x, version.MemoryBase) || Writes(x, query.MemoryBase)))) continue;
                        copies.Add((pe.Offset((uint)copy.IP), function));
                    }
                }
            }
            if (source.AsSpan(start, length).IndexOf(Convert.FromHexString("b7000000")) >= 0)
            {
                var code = Decode(source, pe, function, candidate: true);
                for (int i = 0; i + 1 < code.Count; i++)
                {
                    var select = code[i];
                    if (select.Mnemonic != Mnemonic.Cmovg || select.Op0Kind != OpKind.Register || select.Op1Kind != OpKind.Register) continue;
                    int storeIndex = FindLevelStore(code, i, layout.Config + 0xa8);
                    if (storeIndex < 0) continue;
                    var store = code[storeIndex];
                    if (!ImmediateDefinition(code, i, select.Op0Register, 183, 24, out var original) ||
                        !ImmediateDefinition(code, i, select.Op1Register, 186, 32, out _) ||
                        !HevcGuard(source, pe, code, original, storeIndex, store.MemoryBase) ||
                        !code.Skip(Math.Max(0, original - 8)).Take(i - Math.Max(0, original - 8))
                            .Any(x => StoreImmediate(x, 1) && SameAddress(x, store.MemoryBase, layout.Config + 0xac))) continue;
                    // Only a four-byte MOV immediate is rewritten; derive its
                    // location from the decoder, never a fixed instruction size.
                    var instruction = code[original];
                    var decoder = Decoder.Create(64, new ByteArrayCodeReader(source, pe.Offset((uint)instruction.IP), instruction.Length));
                    decoder.Decode(out var decoded);
                    var constants = decoder.GetConstantOffsets(decoded);
                    Require(constants.ImmediateSize == 4, "HEVC level default is not a bounded 32-bit immediate.");
                    levels.Add(pe.Offset((uint)instruction.IP) + constants.ImmediateOffset);
                }
            }
        }
        Require(copies.Count == 1, $"Decoded HEVC preset copy: expected one verified query/copy relationship, found {copies.Count}.");
        Require(levels.Count == 1, $"Decoded HEVC reconfiguration: expected one verified level write, found {levels.Count}.");
        var levelFunction = functions.Single(f => f.Begin <= pe.Rva(levels[0]) && pe.Rva(levels[0]) < f.End);
        Require(levelFunction.Begin != copies[0].Function.Begin && levelFunction.Begin != initFunction.Begin &&
            copies[0].Function.Begin != initFunction.Begin, "Encoder initialization, preset and reconfiguration must be distinct functions.");
        return (copies[0].Hook, levels[0]);
    }

    private static EncoderLayout DiscoverEncoderLayout(byte[] source, Image pe, List<Instruction> code, uint api, HashSet<uint> imports)
    {
        int a = code.FindIndex(x => x.IP == api);
        Require(a >= 0 && a + 10 < code.Count, "NVENC API initialization is not on an instruction boundary.");
        var version = code[a]; var table = code[a + 4];
        Require(StoreImmediate(version, 0x7102000b) && StackMemory(version) &&
            MoveImmediate(code[a + 1], Register.R8D, 0x9f4) &&
            Lea(code[a + 2], Register.RCX, version.MemoryBase, Displacement(version) + 4) &&
            code[a + 3].Mnemonic == Mnemonic.Call &&
            table.Mnemonic == Mnemonic.Lea && table.Op0Kind == OpKind.Register && PlainMemory(table) &&
            Nonvolatile(table.MemoryBase) && Nonvolatile(table.Op0Register) && table.MemoryBase != table.Op0Register &&
            MoveImmediate(code[a + 5], Register.R8D, 0x9f8) &&
            MoveRegister(code[a + 6], Register.RCX, table.Op0Register) &&
            Lea(code[a + 7], Register.RDX, version.MemoryBase, Displacement(version)) &&
            Memcpy(pe, source, code[a + 8], imports) &&
            MoveRegister(code[a + 9], Register.RCX, table.Op0Register) && code[a + 10].Mnemonic == Mnemonic.Call,
            "Decoded NVENC API table initialization/layout could not be verified.");
        var layouts = new List<EncoderLayout>();
        for (int i = a + 11; i + 12 < code.Count; i++)
        {
            var config = code[i]; var configVersion = code[i + 1]; var pointer = code[i + 3];
            if (config.Mnemonic != Mnemonic.Lea || config.Op0Kind != OpKind.Register || !PlainMemory(config) ||
                config.MemoryBase != table.MemoryBase || !Nonvolatile(config.Op0Register) || config.Op0Register == config.MemoryBase ||
                !StoreImmediate(configVersion, 0xf107000b) || !StackMemory(configVersion) ||
                code[i + 2].Mnemonic != Mnemonic.Xor || code[i + 2].Op0Kind != OpKind.Register || code[i + 2].Op1Kind != OpKind.Register ||
                code[i + 2].Op0Register != Register.EDX || code[i + 2].Op1Register != Register.EDX ||
                !StoreRegister(pointer, config.MemoryBase, Displacement(pointer)) || pointer.MemorySize != MemorySize.UInt64 || pointer.Op1Register != config.Op0Register ||
                !MoveImmediate(code[i + 4], Register.R8D, 0xdfc) ||
                !Lea(code[i + 5], Register.RCX, configVersion.MemoryBase, Displacement(configVersion) + 4) ||
                code[i + 6].Mnemonic != Mnemonic.Call ||
                !MoveRegister(code[i + 7], Register.RCX, config.Op0Register) ||
                !Lea(code[i + 8], Register.RDX, configVersion.MemoryBase, Displacement(configVersion)) ||
                !MoveImmediate(code[i + 9], Register.R8D, 0xe00) || !Memcpy(pe, source, code[i + 10], imports) || !SingleEntry(code, i, i + 10)) continue;
            Require(Displacement(table) > 0 && Displacement(table) < 0x100000 &&
                Displacement(config) > 0 && Displacement(config) + 0xe00 <= Displacement(table) &&
                Displacement(pointer) > 0 && Displacement(pointer) + 8 <= Displacement(config), "Encoder object fields overlap or exceed supported bounds.");
            layouts.Add(new(Displacement(table), Displacement(pointer), Displacement(config)));
        }
        Require(layouts.Count == 1, $"Decoded NVENC config initialization: expected one verified embedded configuration, found {layouts.Count}.");
        return layouts[0];
    }

    private static bool HevcGuard(byte[] source, Image pe, List<Instruction> code, int before, int store, Register obj)
    {
        // Both halves of the actual public HEVC codec GUID must guard the level
        // store, with the same failure edge beyond it. An H.264 level selector or
        // an unrelated 183/186 constant cannot qualify.
        var guid = Convert.FromHexString("88dc0c7922457b4d9425bda9975f7603");
        for (int i = Math.Max(0, before - 16); i + 5 < before; i++)
        {
            var lo = code[i]; var cmpLo = code[i + 1]; var noLo = code[i + 2];
            var hi = code[i + 3]; var cmpHi = code[i + 4]; var noHi = code[i + 5];
            if (!LoadMember(lo, lo.Op0Register) || !LoadMember(hi, hi.Op0Register) || lo.MemoryBase != obj || hi.MemoryBase != obj ||
                Displacement(hi) != Displacement(lo) + 8 || !GuidCompare(cmpLo, lo.Op0Register) || !GuidCompare(cmpHi, hi.Op0Register) ||
                cmpHi.IPRelativeMemoryAddress != cmpLo.IPRelativeMemoryAddress + 8 ||
                noLo.Mnemonic != Mnemonic.Jne || noHi.Mnemonic != Mnemonic.Jne || noLo.NearBranchTarget != noHi.NearBranchTarget ||
                noLo.NearBranchTarget < code[store].NextIP || code.Skip(i + 6).Take(store - i - 6).Any(x => x.FlowControl != FlowControl.Next || Writes(x, obj)) ||
                !SingleEntry(code, i, store)) continue;
            int data = pe.ReadOnlyData(checked((uint)cmpLo.IPRelativeMemoryAddress), 16);
            if (source.AsSpan(data, 16).SequenceEqual(guid)) return true;
        }
        return false;
    }

    private static bool GuidCompare(Instruction x, Register register) => x.Mnemonic == Mnemonic.Cmp &&
        x.Op0Kind == OpKind.Register && x.Op0Register == register && x.Op1Kind == OpKind.Memory && x.IsIPRelativeMemoryOperand;

    private static int FindLevelStore(List<Instruction> code, int select, long field)
    {
        // Instruction scheduling can interleave unrelated loads between selection
        // and store. Follow only this live register, stopping at a clobber or edge.
        var register = code[select].Op0Register;
        for (int i = select + 1; i < Math.Min(code.Count, select + 9); i++)
        {
            var instruction = code[i];
            if (StoreRegister(instruction, instruction.MemoryBase, field) && instruction.MemorySize == MemorySize.UInt32 && instruction.Op1Register == register) return i;
            if (instruction.FlowControl != FlowControl.Next || Writes(instruction, register)) break;
        }
        return -1;
    }

    private static bool ImmediateDefinition(List<Instruction> code, int before, Register register, uint value, int window, out int index)
    {
        for (int i = before - 1; i >= Math.Max(0, before - window); i--)
        {
            if (code[i].FlowControl != FlowControl.Next) break;
            if (!Writes(code[i], register)) continue;
            index = i;
            return MoveImmediate(code[i], register, value);
        }
        index = -1; return false;
    }

    private static bool AddressDefinition(List<Instruction> code, int before, Register register, Register stack, long displacement, int begin)
    {
        for (int i = before - 1; i >= begin; i--)
        {
            if (code[i].FlowControl != FlowControl.Next) return false;
            if (Writes(code[i], register)) return Lea(code[i], register, stack, displacement);
        }
        return false;
    }

    private static bool Writes(Instruction instruction, Register register) => new InstructionInfoFactory().GetInfo(instruction).GetUsedRegisters()
        .Any(x => x.Register.GetFullRegister() == register.GetFullRegister() &&
            x.Access is OpAccess.Write or OpAccess.CondWrite or OpAccess.ReadWrite or OpAccess.ReadCondWrite);

    private static bool Memcpy(Image pe, byte[] source, Instruction instruction, HashSet<uint> imports)
    {
        if (instruction.Code != Code.Call_rel32_64) return false;
        uint rva = checked((uint)instruction.NearBranchTarget);
        if (!pe.IsExecutable(rva, 6)) return false;
        int offset = pe.Offset(rva, 6);
        return source[offset] == 0xff && source[offset + 1] == 0x25 && imports.Contains(pe.Relative(offset + 2, offset + 6));
    }

    private static List<Instruction> Decode(byte[] source, Image pe, Function function, bool candidate = false)
    {
        int length = checked((int)(function.End - function.Begin));
        Require(length <= 1024 * 1024, "Encoder function exceeds the supported analysis bound.");
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(source, pe.Offset(function.Begin), length));
        decoder.IP = function.Begin;
        var result = new List<Instruction>();
        while (decoder.IP < function.End)
        {
            decoder.Decode(out var instruction);
            // A raw seed can occur in unrelated code or inline data. Such a
            // function contributes no candidates; never resynchronize mid-byte.
            if (candidate && (instruction.Code == Code.INVALID || instruction.NextIP > function.End)) return [];
            Require(instruction.Code != Code.INVALID && instruction.NextIP <= function.End, "Invalid instruction in encoder initialization.");
            result.Add(instruction);
        }
        return result;
    }

    private static long Displacement(Instruction x) => unchecked((long)x.MemoryDisplacement64);
    private static bool PlainMemory(Instruction x) => x.MemoryBase != Register.None && x.MemoryIndex == Register.None && !x.IsIPRelativeMemoryOperand &&
        !x.HasSegmentPrefix && !x.HasLockPrefix && !x.HasRepPrefix && !x.HasRepnePrefix;
    private static bool SameAddress(Instruction x, Register register, long displacement) => PlainMemory(x) && x.MemoryBase == register && Displacement(x) == displacement;
    private static bool StackMemory(Instruction x) => PlainMemory(x) && x.MemoryBase is Register.RBP or Register.RSP;
    private static bool StoreImmediate(Instruction x, uint value) => x.Mnemonic == Mnemonic.Mov && x.Op0Kind == OpKind.Memory &&
        x.Op1Kind == OpKind.Immediate32 && x.Immediate32 == value && PlainMemory(x);
    private static bool MoveImmediate(Instruction x, Register register, uint value) => x.Mnemonic == Mnemonic.Mov && x.Op0Kind == OpKind.Register &&
        x.Op0Register == register && x.Op1Kind == OpKind.Immediate32 && x.Immediate32 == value;
    private static bool MoveRegister(Instruction x, Register to, Register from) => x.Mnemonic == Mnemonic.Mov && x.Op0Kind == OpKind.Register &&
        x.Op1Kind == OpKind.Register && x.Op0Register == to && x.Op1Register == from;
    private static bool StoreRegister(Instruction x, Register obj, long offset) => x.Mnemonic == Mnemonic.Mov && x.Op0Kind == OpKind.Memory &&
        x.Op1Kind == OpKind.Register && SameAddress(x, obj, offset);
    private static bool LoadMember(Instruction x, Register to) => x.Mnemonic == Mnemonic.Mov && x.Op0Kind == OpKind.Register && x.Op0Register == to &&
        x.Op1Kind == OpKind.Memory && PlainMemory(x) && x.MemorySize == MemorySize.UInt64;
    private static bool Lea(Instruction x, Register to, Register obj, long offset) => x.Mnemonic == Mnemonic.Lea && x.Op0Kind == OpKind.Register &&
        x.Op0Register == to && x.Op1Kind == OpKind.Memory && SameAddress(x, obj, offset);
    private static bool IsBranch(Instruction x) => x.FlowControl is not (FlowControl.Next or FlowControl.Call or FlowControl.IndirectCall);
    private static bool Nonvolatile(Register register) => register is Register.RBX or Register.RSI or Register.RDI or Register.R12 or Register.R13 or Register.R14 or Register.R15;
    private static bool SingleEntry(List<Instruction> code, int start, int end) => !code.Any(x =>
        x.FlowControl is FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch &&
        x.NearBranchTarget > code[start].IP && x.NearBranchTarget < code[end].NextIP &&
        (x.IP < code[start].IP || x.IP >= code[end].NextIP));
}
