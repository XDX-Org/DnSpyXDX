namespace DnSpyXDX.Decompilation;

/// <summary>Identifies a metadata entity by its declaring assembly's simple name plus its metadata
/// token. Used as the key that ties an IL operand in one module to a definition in another: a caller
/// resolves each operand to a <see cref="RefKey"/>, and a target member computes its own key, so the
/// analyzer can match uses across open assemblies without depending on live type-system identity.</summary>
internal readonly record struct RefKey(string Assembly, int Token);

/// <summary>Walks raw method-body IL and reports the byte offset and 4-byte metadata token of every
/// instruction that carries an entity token (call, callvirt, newobj, ldfld, ldtoken, ldftn, …). String
/// (<c>ldstr</c>) and stand-alone-signature (<c>calli</c>) tokens are skipped. This is the cheap pass
/// that both "Uses" and the reverse-reference index are built from; it never resolves tokens itself.</summary>
internal static class IlReferenceScanner
{
    // Operand length in bytes for every opcode, indexed by the opcode byte. 0xFF marks the switch
    // instruction (variable length). Two-byte (0xFE-prefixed) opcodes use the separate table below.
    private static readonly byte[] SingleOperandLength = BuildSingleOperandLength();
    private static readonly bool[] SingleIsToken = BuildSingleIsToken();
    private static readonly byte[] TwoByteOperandLength = BuildTwoByteOperandLength();
    private static readonly bool[] TwoByteIsToken = BuildTwoByteIsToken();

    /// <summary>Invokes <paramref name="onToken"/> for each token-bearing instruction with its byte offset,
    /// the 4-byte metadata token, and the opcode. Two-byte (0xFE-prefixed) opcodes are reported as
    /// <c>0xFE00 | second</c> so callers can tell, say, <c>ldfld</c> from <c>callvirt</c>.</summary>
    public static void Scan(ReadOnlySpan<byte> il, Action<int, int, int> onToken)
    {
        var position = 0;
        while (position < il.Length)
        {
            var instructionStart = position;
            int opcode = il[position++];
            var code = opcode;
            byte length;
            bool isToken;
            if (opcode == 0xFE)
            {
                if (position >= il.Length) break;
                int second = il[position++];
                if (second >= TwoByteOperandLength.Length) break;
                code = 0xFE00 | second;
                length = TwoByteOperandLength[second];
                isToken = TwoByteIsToken[second];
            }
            else
            {
                length = SingleOperandLength[opcode];
                isToken = SingleIsToken[opcode];
            }

            if (length == 0xFF) // switch: uint32 count followed by count * int32 targets
            {
                if (position + 4 > il.Length) break;
                var count = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(il.Slice(position));
                position += 4 + checked((int)count) * 4;
                continue;
            }

            if (isToken && position + 4 <= il.Length)
            {
                var token = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(il.Slice(position));
                onToken(instructionStart, token, code);
            }
            position += length;
        }
    }

    private static void Fill(byte[] table, int from, int to, byte value)
    {
        for (var index = from; index <= to; index++) table[index] = value;
    }

    private static byte[] BuildSingleOperandLength()
    {
        var length = new byte[256];
        // Defaults are 0 (InlineNone); set only the opcodes that carry an operand.
        length[0x0E] = length[0x0F] = length[0x10] = length[0x11] = length[0x12] = length[0x13] = 1; // ldarg.s..stloc.s (var8)
        length[0x1F] = 1;                                                                             // ldc.i4.s
        length[0x20] = 4;                                                                             // ldc.i4
        length[0x21] = 8;                                                                             // ldc.i8
        length[0x22] = 4;                                                                             // ldc.r4
        length[0x23] = 8;                                                                             // ldc.r8
        length[0x27] = length[0x28] = length[0x29] = 4;                                               // jmp, call, calli
        length[0x2B] = 1;                                                                             // br.s
        Fill(length, 0x2C, 0x37, 1);                                                                  // brfalse.s..blt.un.s
        Fill(length, 0x38, 0x44, 4);                                                                  // br..blt.un (long form)
        length[0x45] = 0xFF;                                                                          // switch
        Fill(length, 0x6F, 0x75, 4);                                                                  // callvirt, cpobj, ldobj, ldstr, newobj, castclass, isinst
        length[0x79] = 4;                                                                             // unbox
        Fill(length, 0x7B, 0x81, 4);                                                                  // ldfld..stobj
        length[0x8C] = length[0x8D] = length[0x8F] = 4;                                               // box, newarr, ldelema
        Fill(length, 0xA3, 0xA5, 4);                                                                  // ldelem, stelem, unbox.any
        length[0xC2] = length[0xC6] = 4;                                                              // refanyval, mkrefany
        length[0xD0] = 4;                                                                             // ldtoken
        length[0xDD] = 4;                                                                             // leave
        length[0xDE] = 1;                                                                             // leave.s
        return length;
    }

    private static bool[] BuildSingleIsToken()
    {
        var token = new bool[256];
        // Every 4-byte token operand except ldstr (0x72, a #US string) and calli (0x29, a StandAloneSig).
        token[0x27] = token[0x28] = true;                 // jmp, call
        SetRange(token, 0x6F, 0x71);                      // callvirt, cpobj, ldobj
        token[0x73] = token[0x74] = token[0x75] = true;   // newobj, castclass, isinst
        token[0x79] = true;                               // unbox
        SetRange(token, 0x7B, 0x81);                      // ldfld..stobj
        token[0x8C] = token[0x8D] = token[0x8F] = true;   // box, newarr, ldelema
        SetRange(token, 0xA3, 0xA5);                      // ldelem, stelem, unbox.any
        token[0xC2] = token[0xC6] = true;                 // refanyval, mkrefany
        token[0xD0] = true;                               // ldtoken
        return token;
    }

    private static byte[] BuildTwoByteOperandLength()
    {
        var length = new byte[32]; // 0xFE 0x00..0x1E
        length[0x06] = length[0x07] = 4;         // ldftn, ldvirtftn
        Fill(length, 0x09, 0x0E, 2);             // ldarg..stloc (var16)
        length[0x12] = 1;                        // unaligned.
        length[0x15] = length[0x16] = 4;         // initobj, constrained.
        length[0x19] = 1;                        // no.
        length[0x1C] = 4;                        // sizeof
        return length;
    }

    private static bool[] BuildTwoByteIsToken()
    {
        var token = new bool[32];
        token[0x06] = token[0x07] = true;        // ldftn, ldvirtftn
        token[0x15] = token[0x16] = true;        // initobj, constrained.
        token[0x1C] = true;                      // sizeof
        return token;
    }

    private static void SetRange(bool[] table, int from, int to)
    {
        for (var index = from; index <= to; index++) table[index] = true;
    }
}
