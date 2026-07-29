using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Emit;

namespace DnSpyXDX.Decompilation;

/// <summary>
/// Chooses a breakpoint-safe instruction inside an ILSpy synthetic sequence-point range.
/// Decompiled ranges can begin in only one arm of a folded branch even when the rendered
/// statement executes after both arms. In that case, bind at the first control-flow join.
/// </summary>
internal sealed class IlBreakpointSelector
{
    private static readonly IReadOnlyDictionary<short, OpCode> OpCodesByValue =
        typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(opcode => opcode.Value);

    private readonly IReadOnlyList<Instruction> instructions;
    private readonly IReadOnlyDictionary<int, IReadOnlyList<int>> predecessors;

    public IlBreakpointSelector(ReadOnlySpan<byte> il)
    {
        instructions = ReadInstructions(il);
        predecessors = BuildPredecessors(instructions);
    }

    public int Select(int startOffset, int endOffset)
    {
        if (startOffset < 0 || endOffset <= startOffset)
            return startOffset;

        foreach (var instruction in instructions)
        {
            if (instruction.Offset <= startOffset ||
                instruction.Offset >= endOffset ||
                !predecessors.TryGetValue(
                    instruction.Offset,
                    out var incoming))
                continue;

            var hasInsidePredecessor = incoming.Any(offset =>
                offset >= startOffset && offset < endOffset);
            var hasOutsidePredecessor = incoming.Any(offset =>
                offset < startOffset || offset >= endOffset);
            if (hasInsidePredecessor && hasOutsidePredecessor)
                return instruction.Offset;
        }

        return startOffset;
    }

    private static IReadOnlyDictionary<int, IReadOnlyList<int>> BuildPredecessors(
        IReadOnlyList<Instruction> values)
    {
        var offsets = values.Select(value => value.Offset).ToHashSet();
        var predecessors = values.ToDictionary(
            value => value.Offset,
            _ => new List<int>());
        foreach (var instruction in values)
        {
            foreach (var target in instruction.BranchTargets)
            {
                if (offsets.Contains(target))
                    predecessors[target].Add(instruction.Offset);
            }
            if (instruction.FallsThrough &&
                offsets.Contains(instruction.NextOffset))
                predecessors[instruction.NextOffset].Add(instruction.Offset);
        }
        return predecessors.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<int>)pair.Value);
    }

    private static IReadOnlyList<Instruction> ReadInstructions(
        ReadOnlySpan<byte> il)
    {
        var instructions = new List<Instruction>();
        var position = 0;
        while (position < il.Length)
        {
            var start = position;
            var first = il[position++];
            short value;
            if (first == 0xFE)
            {
                if (position >= il.Length) break;
                value = unchecked((short)(0xFE00 | il[position++]));
            }
            else
            {
                value = first;
            }
            if (!OpCodesByValue.TryGetValue(value, out var opcode))
                break;

            var operandStart = position;
            int operandLength;
            if (opcode.OperandType == OperandType.InlineSwitch)
            {
                if (position + sizeof(int) > il.Length) break;
                var count = BinaryPrimitives.ReadInt32LittleEndian(
                    il[position..]);
                if (count < 0) break;
                try
                {
                    operandLength = checked(
                        sizeof(int) + count * sizeof(int));
                }
                catch (OverflowException)
                {
                    break;
                }
            }
            else
            {
                operandLength = OperandLength(opcode.OperandType);
            }

            if (operandLength < 0 ||
                operandLength > il.Length - position)
                break;
            position += operandLength;
            var next = position;
            var targets = BranchTargets(
                opcode.OperandType,
                il.Slice(operandStart, operandLength),
                next);
            var fallsThrough = opcode.FlowControl is not (
                FlowControl.Branch or
                FlowControl.Return or
                FlowControl.Throw);
            instructions.Add(new(
                start,
                next,
                targets,
                fallsThrough));
        }
        return instructions;
    }

    private static IReadOnlyList<int> BranchTargets(
        OperandType operandType,
        ReadOnlySpan<byte> operand,
        int nextOffset)
    {
        if (operandType == OperandType.ShortInlineBrTarget)
            return [nextOffset + unchecked((sbyte)operand[0])];
        if (operandType == OperandType.InlineBrTarget)
            return [nextOffset + BinaryPrimitives.ReadInt32LittleEndian(operand)];
        if (operandType != OperandType.InlineSwitch)
            return [];

        var count = BinaryPrimitives.ReadInt32LittleEndian(operand);
        var targets = new int[count];
        for (var index = 0; index < count; index++)
        {
            targets[index] = nextOffset +
                BinaryPrimitives.ReadInt32LittleEndian(
                    operand.Slice(
                        sizeof(int) + index * sizeof(int),
                        sizeof(int)));
        }
        return targets;
    }

    private static int OperandLength(OperandType type) => type switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or
        OperandType.ShortInlineI or
        OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineBrTarget or
        OperandType.InlineField or
        OperandType.InlineI or
        OperandType.InlineMethod or
        OperandType.InlineSig or
        OperandType.InlineString or
        OperandType.InlineTok or
        OperandType.InlineType or
        OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or
        OperandType.InlineR => 8,
        _ => -1
    };

    private sealed record Instruction(
        int Offset,
        int NextOffset,
        IReadOnlyList<int> BranchTargets,
        bool FallsThrough);
}
