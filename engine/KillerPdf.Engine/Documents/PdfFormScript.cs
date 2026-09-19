using System.Globalization;

namespace KillerPdf.Engine.Documents;

/// <summary>The outcome category for one safe AcroForm field script evaluation.</summary>
public enum PdfFormScriptStatus
{
    /// <summary>The script was evaluated by the safe built-in subset.</summary>
    Evaluated,
    /// <summary>The script falls outside the safe subset and was not executed.</summary>
    Unsupported,
    /// <summary>The script is inside the safe subset but could not be evaluated.</summary>
    Failed
}

/// <summary>The outcome of evaluating one AcroForm field script.</summary>
public sealed record PdfFormScriptResult
{
    /// <summary>Gets the evaluation outcome category.</summary>
    public required PdfFormScriptStatus Status { get; init; }
    /// <summary>Gets the resulting field text, or null when no text was produced.</summary>
    public string? Text { get; init; }
    /// <summary>Gets the resulting numeric value, or null when the result is not numeric.</summary>
    public double? Number { get; init; }
    /// <summary>Gets whether the result should be drawn in the negative highlight color.</summary>
    public bool IsRed { get; init; }
    /// <summary>Gets whether a validation script accepted the value.</summary>
    public bool IsValid { get; init; } = true;
    /// <summary>Gets the reason the script was not evaluated, or null on success.</summary>
    public string? Failure { get; init; }
}

/// <summary>
/// Evaluates the built-in AcroForm formatting, calculation, and validation functions,
/// together with a bounded arithmetic subset of field scripts. No general script
/// interpreter is provided and no document, application, or network access is exposed.
/// </summary>
public static class PdfFormScript
{
    private const int MaximumScriptLength = 8192;
    private const int MaximumStatements = 256;
    private const int MaximumDepth = 64;
    private const int MaximumFields = 4096;

    /// <summary>Evaluates a calculation script against the current field values.</summary>
    public static PdfFormScriptResult Calculate(string? script,
        IReadOnlyDictionary<string, string> fieldValues)
    {
        ArgumentNullException.ThrowIfNull(fieldValues);
        if (fieldValues.Count > MaximumFields)
            throw new ArgumentException(
                $"A calculation cannot read more than {MaximumFields} fields.", nameof(fieldValues));
        if (!TryPrepare(script, out string body, out PdfFormScriptResult rejection))
            return rejection;
        if (TryReadCall(body, out string name, out IReadOnlyList<PdfFormScriptArgument> arguments)
            && name == "AFSimple_Calculate")
            return SimpleCalculate(arguments, fieldValues);
        return Interpreter.Run(body, fieldValues);
    }

    /// <summary>Evaluates a format script against a committed field value.</summary>
    public static PdfFormScriptResult Format(string? script, string? value)
    {
        if (!TryPrepare(script, out string body, out PdfFormScriptResult rejection))
            return rejection;
        if (!TryReadCall(body, out string name, out IReadOnlyList<PdfFormScriptArgument> arguments))
            return Unsupported("The format script is not a single built-in format call.");
        try
        {
            return name switch
            {
                "AFNumber_Format" => NumberFormat(arguments, value),
                "AFPercent_Format" => PercentFormat(arguments, value),
                "AFDate_Format" => DateFormat(
                    PdfFormFieldFormat.DatePicture(Integer(arguments, 0)), value),
                "AFDate_FormatEx" => DateFormat(Text(arguments, 0), value),
                "AFTime_Format" => DateFormat(
                    PdfFormFieldFormat.TimePicture(Integer(arguments, 0)), value),
                "AFTime_FormatEx" => DateFormat(Text(arguments, 0), value),
                "AFSpecial_Format" => SpecialFormat(arguments, value),
                _ => Unsupported($"The format function '{name}' is not a built-in format.")
            };
        }
        catch (Exception exception) when (exception is ArgumentException
            or FormatException or InvalidOperationException or OverflowException)
        {
            return Failed(exception.Message);
        }
    }

    /// <summary>Evaluates a validation script against a committed field value.</summary>
    public static PdfFormScriptResult Validate(string? script, string? value)
    {
        if (!TryPrepare(script, out string body, out PdfFormScriptResult rejection))
            return rejection;
        if (!TryReadCall(body, out string name, out IReadOnlyList<PdfFormScriptArgument> arguments))
            return Unsupported("The validation script is not a single built-in validation call.");
        try
        {
            return name switch
            {
                "AFRange_Validate" => RangeValidate(arguments, value),
                "AFSpecial_Keystroke" => new PdfFormScriptResult
                {
                    Status = PdfFormScriptStatus.Evaluated,
                    Text = value,
                    IsValid = PdfFormFieldFormat.IsSpecialValid(
                        value, (PdfFormSpecialFormat)Integer(arguments, 0))
                },
                _ => Unsupported($"The validation function '{name}' is not a built-in check.")
            };
        }
        catch (Exception exception) when (exception is ArgumentException
            or FormatException or InvalidOperationException or OverflowException)
        {
            return Failed(exception.Message);
        }
    }

    private static PdfFormScriptResult NumberFormat(
        IReadOnlyList<PdfFormScriptArgument> arguments, string? value)
    {
        var options = new PdfFormNumberFormatOptions
        {
            DecimalPlaces = Integer(arguments, 0),
            SeparatorStyle = (PdfFormSeparatorStyle)Optional(arguments, 1, 0),
            NegativeStyle = (PdfFormNegativeStyle)Optional(arguments, 2, 0),
            CurrencySymbol = arguments.Count > 4 ? Text(arguments, 4) : string.Empty,
            PrependCurrency = arguments.Count <= 5 || Boolean(arguments, 5)
        };
        if (string.IsNullOrWhiteSpace(value))
            return new PdfFormScriptResult { Status = PdfFormScriptStatus.Evaluated, Text = string.Empty };
        if (!PdfFormFieldFormat.TryParseNumber(value, options.SeparatorStyle, out double number))
            return Failed("The field value is not a number in the declared separator style.");
        return new PdfFormScriptResult
        {
            Status = PdfFormScriptStatus.Evaluated,
            Text = PdfFormFieldFormat.Number(number, options),
            Number = number,
            IsRed = number < 0 && PdfFormFieldFormat.UsesRedNegative(options.NegativeStyle)
        };
    }

    private static PdfFormScriptResult PercentFormat(
        IReadOnlyList<PdfFormScriptArgument> arguments, string? value)
    {
        int decimals = Integer(arguments, 0);
        var style = (PdfFormSeparatorStyle)Optional(arguments, 1, 0);
        if (string.IsNullOrWhiteSpace(value))
            return new PdfFormScriptResult { Status = PdfFormScriptStatus.Evaluated, Text = string.Empty };
        if (!PdfFormFieldFormat.TryParseNumber(value, style, out double number))
            return Failed("The field value is not a number in the declared separator style.");
        return new PdfFormScriptResult
        {
            Status = PdfFormScriptStatus.Evaluated,
            Text = PdfFormFieldFormat.Percent(number, decimals, style),
            Number = number
        };
    }

    private static PdfFormScriptResult DateFormat(string picture, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new PdfFormScriptResult { Status = PdfFormScriptStatus.Evaluated, Text = string.Empty };
        if (!PdfFormFieldFormat.TryParseDate(value, picture, out DateTime parsed))
            return Failed("The field value is not a date the declared picture accepts.");
        return new PdfFormScriptResult
        {
            Status = PdfFormScriptStatus.Evaluated,
            Text = PdfFormFieldFormat.Date(parsed, picture)
        };
    }

    private static PdfFormScriptResult SpecialFormat(
        IReadOnlyList<PdfFormScriptArgument> arguments, string? value)
    {
        var format = (PdfFormSpecialFormat)Integer(arguments, 0);
        return new PdfFormScriptResult
        {
            Status = PdfFormScriptStatus.Evaluated,
            Text = PdfFormFieldFormat.Special(value, format),
            IsValid = PdfFormFieldFormat.IsSpecialValid(value, format)
        };
    }

    private static PdfFormScriptResult RangeValidate(
        IReadOnlyList<PdfFormScriptArgument> arguments, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new PdfFormScriptResult { Status = PdfFormScriptStatus.Evaluated, Text = value };
        if (!PdfFormFieldFormat.TryParseNumber(value,
            PdfFormSeparatorStyle.CommaGroupPointDecimal, out double number))
            return Failed("The field value is not a number.");
        bool valid = true;
        if (Boolean(arguments, 0) && number < Number(arguments, 1)) valid = false;
        if (Boolean(arguments, 2) && number > Number(arguments, 3)) valid = false;
        return new PdfFormScriptResult
        {
            Status = PdfFormScriptStatus.Evaluated,
            Text = value,
            Number = number,
            IsValid = valid,
            Failure = valid ? null : "The value is outside the permitted range."
        };
    }

    private static PdfFormScriptResult SimpleCalculate(
        IReadOnlyList<PdfFormScriptArgument> arguments,
        IReadOnlyDictionary<string, string> fieldValues)
    {
        if (arguments.Count < 2) return Failed("AFSimple_Calculate requires two arguments.");
        string function = Text(arguments, 0).ToUpperInvariant();
        IReadOnlyList<string> names = arguments[1].Items
            ?? [.. Text(arguments, 1).Split(',', StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)];
        var values = new List<double>(names.Count);
        foreach (string name in names)
        {
            if (!fieldValues.TryGetValue(name.Trim(), out string? raw))
                return Failed($"The calculation reads the unknown field '{name.Trim()}'.");
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (!PdfFormFieldFormat.TryParseNumber(raw,
                PdfFormSeparatorStyle.CommaGroupPointDecimal, out double parsed))
                return Failed($"The field '{name.Trim()}' does not hold a number.");
            values.Add(parsed);
        }
        double result;
        if (values.Count == 0) result = 0;
        else result = function switch
        {
            "SUM" => values.Sum(),
            "AVG" => values.Average(),
            "PRD" => values.Aggregate(1d, (product, item) => product * item),
            "MIN" => values.Min(),
            "MAX" => values.Max(),
            _ => double.NaN
        };
        if (double.IsNaN(result) && function is not ("SUM" or "AVG" or "PRD" or "MIN" or "MAX"))
            return Unsupported($"The calculation function '{function}' is not a built-in function.");
        if (!double.IsFinite(result)) return Failed("The calculation result is not finite.");
        return Numeric(result);
    }

    private static bool TryPrepare(string? script, out string body,
        out PdfFormScriptResult rejection)
    {
        body = string.Empty;
        rejection = Unsupported("The script is empty.");
        if (string.IsNullOrWhiteSpace(script)) return false;
        if (script.Length > MaximumScriptLength)
        {
            rejection = Unsupported(
                $"A field script cannot exceed {MaximumScriptLength} characters.");
            return false;
        }
        body = StripComments(script).Trim();
        if (body.Length == 0)
        {
            rejection = Unsupported("The script contains no statements.");
            return false;
        }
        return true;
    }

    private static string StripComments(string script)
    {
        var builder = new System.Text.StringBuilder(script.Length);
        for (int index = 0; index < script.Length;)
        {
            char character = script[index];
            if (character is '"' or '\'')
            {
                int end = index + 1;
                while (end < script.Length && script[end] != character)
                    end += script[end] == '\\' ? 2 : 1;
                end = Math.Min(end + 1, script.Length);
                builder.Append(script, index, end - index);
                index = end;
            }
            else if (character == '/' && index + 1 < script.Length && script[index + 1] == '/')
            {
                while (index < script.Length && script[index] is not ('\n' or '\r')) index++;
            }
            else if (character == '/' && index + 1 < script.Length && script[index + 1] == '*')
            {
                int end = script.IndexOf("*/", index + 2, StringComparison.Ordinal);
                index = end < 0 ? script.Length : end + 2;
                builder.Append(' ');
            }
            else
            {
                builder.Append(character);
                index++;
            }
        }
        return builder.ToString();
    }

    private static bool TryReadCall(string body, out string name,
        out IReadOnlyList<PdfFormScriptArgument> arguments)
    {
        name = string.Empty;
        arguments = [];
        string trimmed = body.TrimEnd();
        while (trimmed.EndsWith(';')) trimmed = trimmed[..^1].TrimEnd();
        int open = trimmed.IndexOf('(');
        if (open <= 0 || !trimmed.EndsWith(')')) return false;
        string identifier = trimmed[..open].Trim();
        if (identifier.Length == 0 || !identifier.All(character =>
            char.IsAsciiLetterOrDigit(character) || character == '_')) return false;
        if (!TrySplitArguments(trimmed[(open + 1)..^1], out List<PdfFormScriptArgument>? parsed))
            return false;
        name = identifier;
        arguments = parsed;
        return true;
    }

    private static bool TrySplitArguments(string text, out List<PdfFormScriptArgument> arguments)
    {
        arguments = [];
        var parts = new List<string>();
        int depth = 0;
        int start = 0;
        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            if (character is '"' or '\'')
            {
                int end = index + 1;
                while (end < text.Length && text[end] != character)
                    end += text[end] == '\\' ? 2 : 1;
                if (end >= text.Length) return false;
                index = end;
            }
            else if (character is '(' or '[') depth++;
            else if (character is ')' or ']') depth--;
            else if (character == ',' && depth == 0)
            {
                parts.Add(text[start..index]);
                start = index + 1;
            }
        }
        if (text.Trim().Length > 0) parts.Add(text[start..]);
        foreach (string part in parts)
        {
            if (!TryReadArgument(part.Trim(), out PdfFormScriptArgument? argument)) return false;
            arguments.Add(argument);
        }
        return true;
    }

    private static bool TryReadArgument(string text, out PdfFormScriptArgument argument)
    {
        argument = new PdfFormScriptArgument();
        if (text.Length == 0) return false;
        if (text.StartsWith("new Array(", StringComparison.Ordinal) && text.EndsWith(')'))
            return TryReadList(text["new Array(".Length..^1], out argument);
        if (text.StartsWith('[') && text.EndsWith(']'))
            return TryReadList(text[1..^1], out argument);
        if (text is "true" or "false")
        {
            argument = new PdfFormScriptArgument { Boolean = text == "true" };
            return true;
        }
        if (text.Length >= 2 && text[0] == text[^1] && text[0] is '"' or '\'')
        {
            argument = new PdfFormScriptArgument { Text = Unescape(text[1..^1]) };
            return true;
        }
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture,
            out double number) && double.IsFinite(number))
        {
            argument = new PdfFormScriptArgument { Number = number };
            return true;
        }
        return false;
    }

    private static bool TryReadList(string text, out PdfFormScriptArgument argument)
    {
        argument = new PdfFormScriptArgument();
        if (!TrySplitArguments(text, out List<PdfFormScriptArgument>? items)) return false;
        if (items.Any(item => item.Text is null)) return false;
        argument = new PdfFormScriptArgument { Items = [.. items.Select(item => item.Text!)] };
        return true;
    }

    private static string Unescape(string text)
    {
        if (!text.Contains('\\')) return text;
        var builder = new System.Text.StringBuilder(text.Length);
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] == '\\' && index + 1 < text.Length) index++;
            builder.Append(text[index]);
        }
        return builder.ToString();
    }

    private static int Integer(IReadOnlyList<PdfFormScriptArgument> arguments, int index)
    {
        double value = Number(arguments, index);
        if (value != Math.Floor(value) || Math.Abs(value) > int.MaxValue)
            throw new FormatException($"Argument {index + 1} is not a whole number.");
        return (int)value;
    }

    private static int Optional(IReadOnlyList<PdfFormScriptArgument> arguments,
        int index, int fallback) => arguments.Count > index ? Integer(arguments, index) : fallback;

    private static double Number(IReadOnlyList<PdfFormScriptArgument> arguments, int index)
    {
        if (arguments.Count <= index)
            throw new FormatException($"The script is missing argument {index + 1}.");
        PdfFormScriptArgument argument = arguments[index];
        if (argument.Number is double number) return number;
        if (argument.Boolean is bool flag) return flag ? 1 : 0;
        if (argument.Text is string text && double.TryParse(text, NumberStyles.Float,
            CultureInfo.InvariantCulture, out double parsed)) return parsed;
        throw new FormatException($"Argument {index + 1} is not a number.");
    }

    private static string Text(IReadOnlyList<PdfFormScriptArgument> arguments, int index)
    {
        if (arguments.Count <= index)
            throw new FormatException($"The script is missing argument {index + 1}.");
        PdfFormScriptArgument argument = arguments[index];
        return argument.Text
            ?? argument.Number?.ToString(CultureInfo.InvariantCulture)
            ?? throw new FormatException($"Argument {index + 1} is not text.");
    }

    private static bool Boolean(IReadOnlyList<PdfFormScriptArgument> arguments, int index)
    {
        if (arguments.Count <= index)
            throw new FormatException($"The script is missing argument {index + 1}.");
        PdfFormScriptArgument argument = arguments[index];
        return argument.Boolean ?? argument.Number is double number && number != 0;
    }

    internal static PdfFormScriptResult Numeric(double value) => new()
    {
        Status = PdfFormScriptStatus.Evaluated,
        Number = value,
        Text = value.ToString("0.############", CultureInfo.InvariantCulture)
    };

    private static PdfFormScriptResult Unsupported(string reason) => new()
    {
        Status = PdfFormScriptStatus.Unsupported,
        Failure = reason
    };

    private static PdfFormScriptResult Failed(string reason) => new()
    {
        Status = PdfFormScriptStatus.Failed,
        Failure = reason
    };

    private sealed record PdfFormScriptArgument
    {
        internal double? Number { get; init; }
        internal string? Text { get; init; }
        internal bool? Boolean { get; init; }
        internal IReadOnlyList<string>? Items { get; init; }
    }

    private sealed class Interpreter
    {
        private readonly string _source;
        private readonly IReadOnlyDictionary<string, string> _fields;
        private readonly Dictionary<string, double> _variables = new(StringComparer.Ordinal);
        private int _index;
        private int _depth;
        private double? _result;

        private Interpreter(string source, IReadOnlyDictionary<string, string> fields)
        {
            _source = source;
            _fields = fields;
        }

        internal static PdfFormScriptResult Run(string source,
            IReadOnlyDictionary<string, string> fields)
        {
            var interpreter = new Interpreter(source, fields);
            try
            {
                interpreter.Statements();
            }
            catch (PdfFormScriptUnsupportedException exception)
            {
                return Unsupported(exception.Message);
            }
            catch (Exception exception) when (exception is FormatException
                or InvalidOperationException or KeyNotFoundException or OverflowException)
            {
                return Failed(exception.Message);
            }
            if (interpreter._result is not double value)
                return Unsupported("The script does not assign a value to event.value.");
            if (!double.IsFinite(value)) return Failed("The calculation result is not finite.");
            return Numeric(value);
        }

        private void Statements()
        {
            for (int count = 0; ; count++)
            {
                if (count >= MaximumStatements)
                    throw new PdfFormScriptUnsupportedException(
                        $"A field script cannot contain more than {MaximumStatements} statements.");
                Space();
                if (_index >= _source.Length) return;
                if (Take(';')) continue;
                if (TakeWord("var"))
                {
                    string name = Identifier();
                    Space();
                    Expect('=');
                    _variables[name] = Expression();
                }
                else
                {
                    string target = Identifier();
                    Space();
                    if (target == "event" && Take('.'))
                    {
                        string member = Identifier();
                        if (member != "value")
                            throw new PdfFormScriptUnsupportedException(
                                $"The script reads or writes the unsupported member event.{member}.");
                        Space();
                        Expect('=');
                        _result = Expression();
                    }
                    else if (_variables.ContainsKey(target))
                    {
                        Space();
                        Expect('=');
                        _variables[target] = Expression();
                    }
                    else throw new PdfFormScriptUnsupportedException(
                        $"The script assigns to the unsupported target '{target}'.");
                }
                EndStatement();
            }
        }

        private void EndStatement()
        {
            bool terminated = false;
            while (_index < _source.Length)
            {
                char character = _source[_index];
                if (character is ';' or '\n' or '\r')
                {
                    terminated = true;
                    _index++;
                }
                else if (char.IsWhiteSpace(character)) _index++;
                else break;
            }
            if (_index < _source.Length && !terminated)
                throw new PdfFormScriptUnsupportedException(
                    "The script contains a statement the safe subset does not support.");
        }

        private double Expression()
        {
            Enter();
            try
            {
                double condition = LogicalOr();
                Space();
                if (!Take('?')) return condition;
                double whenTrue = Expression();
                Space();
                Expect(':');
                double whenFalse = Expression();
                return condition != 0 ? whenTrue : whenFalse;
            }
            finally { _depth--; }
        }

        private double LogicalOr()
        {
            double value = LogicalAnd();
            while (true)
            {
                Space();
                if (Take("||"))
                {
                    double right = LogicalAnd();
                    value = value != 0 || right != 0 ? 1 : 0;
                }
                else return value;
            }
        }

        private double LogicalAnd()
        {
            double value = Equality();
            while (true)
            {
                Space();
                if (Take("&&"))
                {
                    double right = Equality();
                    value = value != 0 && right != 0 ? 1 : 0;
                }
                else return value;
            }
        }

        private double Equality()
        {
            double value = Relational();
            while (true)
            {
                Space();
                if (Take("===") || Take("==")) value = value == Relational() ? 1 : 0;
                else if (Take("!==") || Take("!=")) value = value != Relational() ? 1 : 0;
                else return value;
            }
        }

        private double Relational()
        {
            double value = Additive();
            while (true)
            {
                Space();
                if (Take("<=")) value = value <= Additive() ? 1 : 0;
                else if (Take(">=")) value = value >= Additive() ? 1 : 0;
                else if (Take('<')) value = value < Additive() ? 1 : 0;
                else if (Take('>')) value = value > Additive() ? 1 : 0;
                else return value;
            }
        }

        private double Additive()
        {
            double value = Multiplicative();
            while (true)
            {
                Space();
                if (_index < _source.Length && _source[_index] == '+' && !Peek("++"))
                {
                    _index++;
                    value += Multiplicative();
                }
                else if (_index < _source.Length && _source[_index] == '-' && !Peek("--"))
                {
                    _index++;
                    value -= Multiplicative();
                }
                else return value;
            }
        }

        private double Multiplicative()
        {
            double value = Unary();
            while (true)
            {
                Space();
                if (Take('*')) value *= Unary();
                else if (Take('/'))
                {
                    double divisor = Unary();
                    if (divisor == 0)
                        throw new InvalidOperationException("The calculation divides by zero.");
                    value /= divisor;
                }
                else if (Take('%'))
                {
                    double divisor = Unary();
                    if (divisor == 0)
                        throw new InvalidOperationException("The calculation divides by zero.");
                    value %= divisor;
                }
                else return value;
            }
        }

        private double Unary()
        {
            Space();
            if (Take('-')) return -Unary();
            if (Take('+')) return Unary();
            if (Take('!')) return Unary() == 0 ? 1 : 0;
            return Primary();
        }

        private double Primary()
        {
            Enter();
            try
            {
                Space();
                if (_index >= _source.Length)
                    throw new FormatException("The calculation ends before its expression.");
                if (Take('('))
                {
                    double value = Expression();
                    Space();
                    Expect(')');
                    return value;
                }
                char character = _source[_index];
                if (char.IsAsciiDigit(character) || character == '.') return NumberLiteral();
                if (char.IsAsciiLetter(character) || character is '_' or '$') return Reference();
                throw new PdfFormScriptUnsupportedException(
                    $"The calculation contains the unsupported character '{character}'.");
            }
            finally { _depth--; }
        }

        private double NumberLiteral()
        {
            int start = _index;
            while (_index < _source.Length
                && (char.IsAsciiDigit(_source[_index]) || _source[_index] == '.')) _index++;
            if (_index < _source.Length && _source[_index] is 'e' or 'E')
            {
                _index++;
                if (_index < _source.Length && _source[_index] is '+' or '-') _index++;
                while (_index < _source.Length && char.IsAsciiDigit(_source[_index])) _index++;
            }
            if (!double.TryParse(_source[start.._index], NumberStyles.Float,
                CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
                throw new FormatException("The calculation contains an invalid number.");
            return value;
        }

        private double Reference()
        {
            string name = Identifier();
            Space();
            if (name == "this")
            {
                Expect('.');
                name = Identifier();
                Space();
            }
            if (name == "Math")
            {
                Expect('.');
                return MathCall(Identifier());
            }
            if (name == "getField") return FieldValue();
            if (name is "Number" or "parseFloat" or "parseInt")
            {
                Expect('(');
                double value = Expression();
                Space();
                Expect(')');
                return name == "parseInt" ? Math.Truncate(value) : value;
            }
            if (name == "event")
            {
                Expect('.');
                string member = Identifier();
                if (member != "value")
                    throw new PdfFormScriptUnsupportedException(
                        $"The calculation reads the unsupported member event.{member}.");
                return _result ?? 0;
            }
            if (_variables.TryGetValue(name, out double variable)) return variable;
            throw new PdfFormScriptUnsupportedException(
                $"The calculation reads the unsupported name '{name}'.");
        }

        private double FieldValue()
        {
            Space();
            Expect('(');
            Space();
            if (_index >= _source.Length || _source[_index] is not ('"' or '\''))
                throw new PdfFormScriptUnsupportedException(
                    "A field reference requires a literal field name.");
            char quote = _source[_index++];
            int start = _index;
            while (_index < _source.Length && _source[_index] != quote) _index++;
            if (_index >= _source.Length)
                throw new FormatException("A field name is not terminated.");
            string name = Unescape(_source[start.._index]);
            _index++;
            Space();
            Expect(')');
            Space();
            Expect('.');
            string member = Identifier();
            if (member is not ("value" or "valueAsString"))
                throw new PdfFormScriptUnsupportedException(
                    $"The calculation reads the unsupported field member '{member}'.");
            if (!_fields.TryGetValue(name, out string? raw))
                throw new KeyNotFoundException(
                    $"The calculation reads the unknown field '{name}'.");
            if (string.IsNullOrWhiteSpace(raw)) return 0;
            if (!PdfFormFieldFormat.TryParseNumber(raw,
                PdfFormSeparatorStyle.CommaGroupPointDecimal, out double value))
                throw new FormatException($"The field '{name}' does not hold a number.");
            return value;
        }

        private double MathCall(string name)
        {
            Space();
            Expect('(');
            var arguments = new List<double>();
            Space();
            if (!Take(')'))
            {
                do { arguments.Add(Expression()); Space(); } while (Take(','));
                Expect(')');
            }
            double First() => arguments.Count == 1 ? arguments[0]
                : throw new FormatException($"Math.{name} requires one argument.");
            double Second() => arguments.Count == 2 ? arguments[1]
                : throw new FormatException($"Math.{name} requires two arguments.");
            return name switch
            {
                "round" => Math.Round(First(), MidpointRounding.AwayFromZero),
                "floor" => Math.Floor(First()),
                "ceil" => Math.Ceiling(First()),
                "abs" => Math.Abs(First()),
                "trunc" => Math.Truncate(First()),
                "sqrt" => First() >= 0 ? Math.Sqrt(First())
                    : throw new InvalidOperationException("Math.sqrt requires a value of zero or more."),
                "pow" => Math.Pow(arguments.Count == 2 ? arguments[0] : First(), Second()),
                "min" => arguments.Count > 0 ? arguments.Min()
                    : throw new FormatException("Math.min requires an argument."),
                "max" => arguments.Count > 0 ? arguments.Max()
                    : throw new FormatException("Math.max requires an argument."),
                _ => throw new PdfFormScriptUnsupportedException(
                    $"The calculation calls the unsupported function Math.{name}.")
            };
        }

        private string Identifier()
        {
            Space();
            int start = _index;
            while (_index < _source.Length && (char.IsAsciiLetterOrDigit(_source[_index])
                || _source[_index] is '_' or '$')) _index++;
            if (_index == start)
                throw new PdfFormScriptUnsupportedException(
                    "The script contains a statement the safe subset does not support.");
            return _source[start.._index];
        }

        private void Enter()
        {
            if (++_depth > MaximumDepth)
                throw new PdfFormScriptUnsupportedException(
                    $"A field script cannot nest more than {MaximumDepth} levels.");
        }

        private void Space()
        {
            while (_index < _source.Length && char.IsWhiteSpace(_source[_index])) _index++;
        }

        private bool Peek(string value) =>
            _source.AsSpan(_index).StartsWith(value, StringComparison.Ordinal);

        private bool Take(char value)
        {
            Space();
            if (_index >= _source.Length || _source[_index] != value) return false;
            _index++;
            return true;
        }

        private bool Take(string value)
        {
            Space();
            if (!Peek(value)) return false;
            _index += value.Length;
            return true;
        }

        private bool TakeWord(string value)
        {
            Space();
            if (!Peek(value)) return false;
            int end = _index + value.Length;
            if (end < _source.Length && (char.IsAsciiLetterOrDigit(_source[end])
                || _source[end] is '_' or '$')) return false;
            _index = end;
            return true;
        }

        private void Expect(char value)
        {
            if (!Take(value))
                throw new PdfFormScriptUnsupportedException(
                    $"The script is missing the expected '{value}' character.");
        }
    }

    private sealed class PdfFormScriptUnsupportedException(string message)
        : InvalidOperationException(message);
}
