using System.Globalization;
using KillerPdf.Engine.Syntax;

namespace KillerPdf.Engine.Rendering;

public sealed partial class PdfPageRenderer
{
    /// <summary>
    /// A PostScript calculator (FunctionType 4) program compiled to flat operation arrays and
    /// evaluated on a typed value stack, so per-pixel tint and shading evaluation does not box
    /// values, dispatch on operator strings, or copy the stack for copy and roll.
    /// </summary>
    internal sealed class CalculatorProgram
    {
        private const int MaximumStack = 256;
        private const int MaximumInstructions = 4096;

        private enum Opcode : byte
        {
            PushNumber, PushBoolean, PushProcedure,
            Abs, Add, And, Atan, Bitshift, Ceiling, Copy, Cos, Cvi, Cvr, Div, Dup, Eq, Exch,
            Exp, False, Floor, Ge, Gt, Idiv, If, IfElse, Index, Le, Ln, Log, Lt, Mod, Mul,
            Ne, Neg, Not, Or, Pop, Roll, Round, Sin, Sqrt, Sub, True, Truncate, Xor,
            // An operator this engine does not implement; it fails only when executed.
            Unknown
        }

        private enum Kind : byte { Number, Boolean, Procedure }

        private readonly struct Op(Opcode code, double number = 0, int procedure = -1)
        {
            public readonly Opcode Code = code;
            public readonly double Number = number;
            public readonly int Procedure = procedure;
        }

        private readonly record struct Value(Kind Kind, double Number);

        private static readonly Dictionary<string, Opcode> Operators = new(StringComparer.Ordinal)
        {
            ["abs"] = Opcode.Abs, ["add"] = Opcode.Add, ["and"] = Opcode.And,
            ["atan"] = Opcode.Atan, ["bitshift"] = Opcode.Bitshift, ["ceiling"] = Opcode.Ceiling,
            ["copy"] = Opcode.Copy, ["cos"] = Opcode.Cos, ["cvi"] = Opcode.Cvi, ["cvr"] = Opcode.Cvr,
            ["div"] = Opcode.Div, ["dup"] = Opcode.Dup, ["eq"] = Opcode.Eq, ["exch"] = Opcode.Exch,
            ["exp"] = Opcode.Exp, ["false"] = Opcode.False, ["floor"] = Opcode.Floor,
            ["ge"] = Opcode.Ge, ["gt"] = Opcode.Gt, ["idiv"] = Opcode.Idiv, ["if"] = Opcode.If,
            ["ifelse"] = Opcode.IfElse, ["index"] = Opcode.Index, ["le"] = Opcode.Le,
            ["ln"] = Opcode.Ln, ["log"] = Opcode.Log, ["lt"] = Opcode.Lt, ["mod"] = Opcode.Mod,
            ["mul"] = Opcode.Mul, ["ne"] = Opcode.Ne, ["neg"] = Opcode.Neg, ["not"] = Opcode.Not,
            ["or"] = Opcode.Or, ["pop"] = Opcode.Pop, ["roll"] = Opcode.Roll,
            ["round"] = Opcode.Round, ["sin"] = Opcode.Sin, ["sqrt"] = Opcode.Sqrt,
            ["sub"] = Opcode.Sub, ["true"] = Opcode.True, ["truncate"] = Opcode.Truncate,
            ["xor"] = Opcode.Xor
        };

        [ThreadStatic]
        private static Value[]? _sharedStack;

        private readonly List<Op[]> _procedures = [];
        private readonly List<string> _unknownOperators = [];
        private readonly string _description;
        private int _instructionCount;

        private CalculatorProgram(string description)
        {
            _description = description;
        }

        /// <summary>Compiles the program body after its opening brace was consumed.</summary>
        internal static CalculatorProgram Compile(PdfTokenizer tokenizer, string description)
        {
            var program = new CalculatorProgram(description);
            // Slot 0 is the main procedure; nested procedures register while it is read.
            program._procedures.Add([]);
            program._procedures[0] = program.ReadProcedure(tokenizer);
            return program;
        }

        private Op[] ReadProcedure(PdfTokenizer tokenizer)
        {
            var procedure = new List<Op>();
            while (true)
            {
                PdfToken token = tokenizer.Read();
                if (token.Kind == PdfTokenKind.BraceEnd) return [.. procedure];
                if (token.Kind == PdfTokenKind.EndOfInput || ++_instructionCount > MaximumInstructions)
                    throw new FormatException($"A {_description} program is invalid or too large.");
                switch (token.Kind)
                {
                    case PdfTokenKind.Integer:
                    case PdfTokenKind.Real:
                        procedure.Add(new Op(Opcode.PushNumber, double.Parse(
                            token.ValueAsLatin1(), CultureInfo.InvariantCulture)));
                        break;
                    case PdfTokenKind.Boolean:
                        procedure.Add(new Op(Opcode.PushBoolean,
                            token.Value.Span.SequenceEqual("true"u8) ? 1 : 0));
                        break;
                    case PdfTokenKind.Keyword:
                        string keyword = token.ValueAsLatin1();
                        if (Operators.TryGetValue(keyword, out Opcode code))
                            procedure.Add(new Op(code));
                        else
                        {
                            _unknownOperators.Add(keyword);
                            procedure.Add(new Op(Opcode.Unknown, procedure: _unknownOperators.Count - 1));
                        }
                        break;
                    case PdfTokenKind.BraceStart:
                        // Reserve the slot first so nested procedures keep stable indexes.
                        int index = _procedures.Count;
                        _procedures.Add([]);
                        _procedures[index] = ReadProcedure(tokenizer);
                        procedure.Add(new Op(Opcode.PushProcedure, procedure: index));
                        break;
                    default:
                        throw new NotSupportedException();
                }
            }
        }

        /// <summary>Evaluates the program, clamping inputs to the domain and outputs to the range.</summary>
        internal void Evaluate(ReadOnlySpan<double> inputs, ReadOnlySpan<double> domain,
            ReadOnlySpan<double> range, Span<double> outputs)
        {
            Value[] stack = _sharedStack ??= new Value[MaximumStack];
            int count = 0;
            for (int index = 0; index < inputs.Length; index++)
                Push(stack, ref count, new Value(Kind.Number,
                    Math.Clamp(inputs[index], domain[index * 2], domain[index * 2 + 1])));
            Execute(_procedures[0], stack, ref count);
            if (count < outputs.Length)
                throw new FormatException($"A {_description} program produced too few values.");
            int outputStart = count - outputs.Length;
            for (int index = 0; index < outputs.Length; index++)
            {
                Value value = stack[outputStart + index];
                if (value.Kind != Kind.Number)
                    throw new FormatException($"A {_description} calculator output is not numeric.");
                outputs[index] = Math.Clamp(value.Number, range[index * 2], range[index * 2 + 1]);
            }
        }

        private void Push(Value[] stack, ref int count, Value value)
        {
            if (value.Kind == Kind.Number && !double.IsFinite(value.Number) || count >= MaximumStack)
                throw new FormatException($"A {_description} calculator stack is invalid.");
            stack[count++] = value;
        }

        private Value PopValue(Value[] stack, ref int count)
        {
            if (count == 0)
                throw new FormatException($"A {_description} calculator stack underflowed.");
            return stack[--count];
        }

        private double Pop(Value[] stack, ref int count)
        {
            Value value = PopValue(stack, ref count);
            if (value.Kind == Kind.Number) return value.Number;
            throw new FormatException($"A {_description} calculator value is not numeric.");
        }

        private bool PopBoolean(Value[] stack, ref int count)
        {
            Value value = PopValue(stack, ref count);
            if (value.Kind == Kind.Boolean) return value.Number != 0;
            throw new FormatException($"A {_description} calculator value is not Boolean.");
        }

        private int PopProcedure(Value[] stack, ref int count)
        {
            Value value = PopValue(stack, ref count);
            if (value.Kind == Kind.Procedure) return (int)value.Number;
            throw new FormatException($"A {_description} calculator value is not a procedure.");
        }

        private int PopInteger(Value[] stack, ref int count) => Integer(Pop(stack, ref count));

        private int Integer(double value)
        {
            if (value < int.MinValue || value > int.MaxValue || value != Math.Truncate(value))
                throw new FormatException($"A {_description} calculator integer is invalid.");
            return (int)value;
        }

        private static Value Number(double value) => new(Kind.Number, value);
        private static Value Boolean(bool value) => new(Kind.Boolean, value ? 1 : 0);

        private static bool ValuesEqual(Value left, Value right) =>
            left.Kind == right.Kind && left.Number == right.Number;

        // Procedures cannot refer to themselves, so recursion depth is bounded by the
        // program's lexical nesting, which the instruction limit already bounds.
        private void Execute(Op[] procedure, Value[] stack, ref int count)
        {
            foreach (Op op in procedure)
            {
                switch (op.Code)
                {
                    case Opcode.PushNumber: Push(stack, ref count, Number(op.Number)); break;
                    case Opcode.PushBoolean: Push(stack, ref count, Boolean(op.Number != 0)); break;
                    case Opcode.PushProcedure:
                        Push(stack, ref count, new Value(Kind.Procedure, op.Procedure)); break;
                    case Opcode.Abs: Push(stack, ref count, Number(Math.Abs(Pop(stack, ref count)))); break;
                    case Opcode.Add:
                    {
                        double b = Pop(stack, ref count);
                        Push(stack, ref count, Number(Pop(stack, ref count) + b));
                        break;
                    }
                    case Opcode.And: BinaryLogical(stack, ref count, op.Code); break;
                    case Opcode.Atan:
                    {
                        double denominator = Pop(stack, ref count);
                        double numerator = Pop(stack, ref count);
                        double angle = Math.Atan2(numerator, denominator) * 180 / Math.PI;
                        Push(stack, ref count, Number(angle < 0 ? angle + 360 : angle));
                        break;
                    }
                    case Opcode.Bitshift:
                    {
                        int shift = PopInteger(stack, ref count), value = PopInteger(stack, ref count);
                        Push(stack, ref count, Number(shift >= 32 ? 0 : shift <= -32 ? value < 0 ? -1 : 0
                            : shift >= 0 ? value << shift : value >> -shift));
                        break;
                    }
                    case Opcode.Ceiling: Push(stack, ref count, Number(Math.Ceiling(Pop(stack, ref count)))); break;
                    case Opcode.Cos: Push(stack, ref count, Number(Math.Cos(Pop(stack, ref count) * Math.PI / 180))); break;
                    case Opcode.Copy:
                    {
                        int copies = PopInteger(stack, ref count);
                        if (copies < 0 || copies > count || count + copies > MaximumStack)
                            throw new FormatException($"A {_description} calculator copy is invalid.");
                        int start = count - copies;
                        for (int index = 0; index < copies; index++)
                            Push(stack, ref count, stack[start + index]);
                        break;
                    }
                    case Opcode.Cvi: Push(stack, ref count, Number(Math.Truncate(Pop(stack, ref count)))); break;
                    case Opcode.Cvr: break;
                    case Opcode.Div:
                    {
                        double b = Pop(stack, ref count);
                        Push(stack, ref count, Number(Pop(stack, ref count) / b));
                        break;
                    }
                    case Opcode.Dup:
                    {
                        Value value = PopValue(stack, ref count);
                        Push(stack, ref count, value);
                        Push(stack, ref count, value);
                        break;
                    }
                    case Opcode.Eq:
                    {
                        Value right = PopValue(stack, ref count);
                        Push(stack, ref count, Boolean(ValuesEqual(PopValue(stack, ref count), right)));
                        break;
                    }
                    case Opcode.Exch:
                    {
                        Value b = PopValue(stack, ref count), a = PopValue(stack, ref count);
                        Push(stack, ref count, b);
                        Push(stack, ref count, a);
                        break;
                    }
                    case Opcode.Exp:
                    {
                        double exponent = Pop(stack, ref count);
                        Push(stack, ref count, Number(Math.Pow(Pop(stack, ref count), exponent)));
                        break;
                    }
                    case Opcode.False: Push(stack, ref count, Boolean(false)); break;
                    case Opcode.Floor: Push(stack, ref count, Number(Math.Floor(Pop(stack, ref count)))); break;
                    case Opcode.Idiv:
                    {
                        int b = PopInteger(stack, ref count);
                        int a = PopInteger(stack, ref count);
                        Push(stack, ref count, Number(a / b));
                        break;
                    }
                    case Opcode.Index:
                    {
                        int index = PopInteger(stack, ref count);
                        if (index < 0 || index >= count)
                            throw new FormatException($"A {_description} calculator index is invalid.");
                        Push(stack, ref count, stack[count - index - 1]);
                        break;
                    }
                    case Opcode.If:
                    {
                        int procedureIndex = PopProcedure(stack, ref count);
                        if (PopBoolean(stack, ref count))
                            Execute(_procedures[procedureIndex], stack, ref count);
                        break;
                    }
                    case Opcode.IfElse:
                    {
                        int whenFalse = PopProcedure(stack, ref count);
                        int whenTrue = PopProcedure(stack, ref count);
                        Execute(_procedures[PopBoolean(stack, ref count) ? whenTrue : whenFalse],
                            stack, ref count);
                        break;
                    }
                    case Opcode.Ln: Push(stack, ref count, Number(Math.Log(Pop(stack, ref count)))); break;
                    case Opcode.Log: Push(stack, ref count, Number(Math.Log10(Pop(stack, ref count)))); break;
                    case Opcode.Mod:
                    {
                        int b = PopInteger(stack, ref count);
                        int a = PopInteger(stack, ref count);
                        Push(stack, ref count, Number(a % b));
                        break;
                    }
                    case Opcode.Mul:
                    {
                        double b = Pop(stack, ref count);
                        Push(stack, ref count, Number(Pop(stack, ref count) * b));
                        break;
                    }
                    case Opcode.Neg: Push(stack, ref count, Number(-Pop(stack, ref count))); break;
                    case Opcode.Ne:
                    {
                        Value right = PopValue(stack, ref count);
                        Push(stack, ref count, Boolean(!ValuesEqual(PopValue(stack, ref count), right)));
                        break;
                    }
                    case Opcode.Not:
                    {
                        Value value = PopValue(stack, ref count);
                        if (value.Kind == Kind.Boolean) Push(stack, ref count, Boolean(value.Number == 0));
                        else if (value.Kind == Kind.Number)
                            Push(stack, ref count, Number(~Integer(value.Number)));
                        else throw new FormatException(
                            $"A {_description} calculator not value is invalid.");
                        break;
                    }
                    case Opcode.Or: BinaryLogical(stack, ref count, op.Code); break;
                    case Opcode.Lt:
                    {
                        double b = Pop(stack, ref count);
                        Push(stack, ref count, Boolean(Pop(stack, ref count) < b));
                        break;
                    }
                    case Opcode.Le:
                    {
                        double b = Pop(stack, ref count);
                        Push(stack, ref count, Boolean(Pop(stack, ref count) <= b));
                        break;
                    }
                    case Opcode.Gt:
                    {
                        double b = Pop(stack, ref count);
                        Push(stack, ref count, Boolean(Pop(stack, ref count) > b));
                        break;
                    }
                    case Opcode.Ge:
                    {
                        double b = Pop(stack, ref count);
                        Push(stack, ref count, Boolean(Pop(stack, ref count) >= b));
                        break;
                    }
                    case Opcode.Pop: PopValue(stack, ref count); break;
                    case Opcode.Roll:
                    {
                        int shift = PopInteger(stack, ref count), rolled = PopInteger(stack, ref count);
                        if (rolled < 0 || rolled > count)
                            throw new FormatException($"A {_description} calculator roll is invalid.");
                        if (rolled == 0) break;
                        shift %= rolled;
                        if (shift < 0) shift += rolled;
                        if (shift == 0) break;
                        // Rotate in place with three reversals: no copy of the rolled values.
                        int start = count - rolled;
                        Array.Reverse(stack, start, rolled);
                        Array.Reverse(stack, start, shift);
                        Array.Reverse(stack, start + shift, rolled - shift);
                        break;
                    }
                    case Opcode.Round:
                        Push(stack, ref count, Number(Math.Round(Pop(stack, ref count),
                            MidpointRounding.AwayFromZero)));
                        break;
                    case Opcode.Sin: Push(stack, ref count, Number(Math.Sin(Pop(stack, ref count) * Math.PI / 180))); break;
                    case Opcode.Sqrt: Push(stack, ref count, Number(Math.Sqrt(Pop(stack, ref count)))); break;
                    case Opcode.Sub:
                    {
                        double b = Pop(stack, ref count);
                        Push(stack, ref count, Number(Pop(stack, ref count) - b));
                        break;
                    }
                    case Opcode.True: Push(stack, ref count, Boolean(true)); break;
                    case Opcode.Truncate: Push(stack, ref count, Number(Math.Truncate(Pop(stack, ref count)))); break;
                    case Opcode.Xor: BinaryLogical(stack, ref count, op.Code); break;
                    default: throw new NotSupportedException(
                        $"Calculator operator {_unknownOperators[op.Procedure]} is not implemented.");
                }
            }
        }

        private void BinaryLogical(Value[] stack, ref int count, Opcode code)
        {
            Value right = PopValue(stack, ref count), left = PopValue(stack, ref count);
            if (left.Kind == Kind.Boolean && right.Kind == Kind.Boolean)
            {
                bool a = left.Number != 0, b = right.Number != 0;
                Push(stack, ref count, Boolean(code switch
                {
                    Opcode.And => a && b,
                    Opcode.Or => a || b,
                    _ => a ^ b
                }));
            }
            else if (left.Kind == Kind.Number && right.Kind == Kind.Number)
            {
                int a = Integer(left.Number), b = Integer(right.Number);
                Push(stack, ref count, Number(code switch
                {
                    Opcode.And => a & b,
                    Opcode.Or => a | b,
                    _ => a ^ b
                }));
            }
            else throw new FormatException(
                $"A {_description} calculator logical value is invalid.");
        }
    }
}
