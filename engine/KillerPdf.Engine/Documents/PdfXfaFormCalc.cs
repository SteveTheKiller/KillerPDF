using System.Globalization;

namespace KillerPdf.Engine.Documents;

/// <summary>Evaluates a bounded arithmetic subset of FormCalc without application access.</summary>
public static class PdfXfaFormCalc
{
    /// <summary>Evaluates numeric literals, variables, arithmetic, and safe numeric functions.</summary>
    public static double Evaluate(string expression,
        IReadOnlyDictionary<string, double>? variables = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        if (expression.Length > 4096)
            throw new ArgumentException("A FormCalc expression cannot exceed 4096 characters.", nameof(expression));
        var parser = new Parser(expression, variables
            ?? new Dictionary<string, double>(StringComparer.Ordinal));
        double result = parser.Parse();
        if (!double.IsFinite(result))
            throw new InvalidOperationException("The FormCalc result is not finite.");
        return result;
    }

    private sealed class Parser
    {
        private readonly string _source;
        private readonly IReadOnlyDictionary<string, double> _variables;
        private int _index;
        private int _depth;

        internal Parser(string source, IReadOnlyDictionary<string, double> variables)
        {
            _source = source;
            _variables = variables;
            if (variables.Any(item => string.IsNullOrWhiteSpace(item.Key)
                || !double.IsFinite(item.Value)))
                throw new ArgumentException("FormCalc variables require names and finite values.", nameof(variables));
        }

        internal double Parse()
        {
            double value = Expression();
            WhiteSpace();
            if (_index != _source.Length)
                throw Error("The FormCalc expression contains unsupported syntax.");
            return value;
        }

        private double Expression()
        {
            Enter();
            try
            {
                double value = LogicalOr();
                return value;
            }
            finally { _depth--; }
        }

        private double LogicalOr()
        {
            double value = LogicalAnd();
            while (true)
            {
                WhiteSpace();
                if (Take('|') || TakeKeyword("or"))
                {
                    double right = LogicalAnd();
                    value = Truth(value) || Truth(right) ? 1 : 0;
                }
                else return value;
            }
        }

        private double LogicalAnd()
        {
            double value = Equality();
            while (true)
            {
                WhiteSpace();
                if (Take('&') || TakeKeyword("and"))
                {
                    double right = Equality();
                    value = Truth(value) && Truth(right) ? 1 : 0;
                }
                else return value;
            }
        }

        private double Equality()
        {
            double value = Relational();
            while (true)
            {
                WhiteSpace();
                if (Take("==") || TakeKeyword("eq"))
                    value = value == Relational() ? 1 : 0;
                else if (Take("<>") || TakeKeyword("ne"))
                    value = value != Relational() ? 1 : 0;
                else return value;
            }
        }

        private double Relational()
        {
            double value = Additive();
            while (true)
            {
                WhiteSpace();
                if (Take("<=") || TakeKeyword("le"))
                    value = value <= Additive() ? 1 : 0;
                else if (Take(">=") || TakeKeyword("ge"))
                    value = value >= Additive() ? 1 : 0;
                else if ((!Peek("<>") && Take('<')) || TakeKeyword("lt"))
                    value = value < Additive() ? 1 : 0;
                else if (Take('>') || TakeKeyword("gt"))
                    value = value > Additive() ? 1 : 0;
                else return value;
            }
        }

        private double Additive()
        {
            double value = Term();
            while (true)
            {
                WhiteSpace();
                if (Take('+')) value = Checked(value + Term());
                else if (Take('-')) value = Checked(value - Term());
                else return value;
            }
        }

        private double Term()
        {
            double value = Unary();
            while (true)
            {
                WhiteSpace();
                if (Take('*')) value = Checked(value * Unary());
                else if (Take('/'))
                {
                    double divisor = Unary();
                    if (divisor == 0) throw Error("FormCalc division by zero is not supported.");
                    value = Checked(value / divisor);
                }
                else return value;
            }
        }

        private double Unary()
        {
            WhiteSpace();
            if (TakeKeyword("not")) return Truth(Unary()) ? 0 : 1;
            if (Take('+')) return Unary();
            if (Take('-')) return Checked(-Unary());
            return Primary();
        }

        private double Primary()
        {
            WhiteSpace();
            if (Take('('))
            {
                double value = Expression();
                WhiteSpace();
                if (!Take(')')) throw Error("A FormCalc parenthesis is not closed.");
                return value;
            }
            if (_index < _source.Length
                && (char.IsAsciiLetter(_source[_index]) || _source[_index] is '_' or '$'))
            {
                int start = _index++;
                while (_index < _source.Length && (char.IsAsciiLetterOrDigit(_source[_index])
                    || _source[_index] is '_' or '$' or '.')) _index++;
                string name = _source[start.._index];
                WhiteSpace();
                if (_index < _source.Length && _source[_index] == '(')
                    return Function(name);
                return _variables.TryGetValue(name, out double value)
                    ? value : throw new KeyNotFoundException(
                        $"The FormCalc variable '{name}' was not supplied.");
            }
            int numberStart = _index;
            while (_index < _source.Length && (char.IsAsciiDigit(_source[_index])
                || _source[_index] is '.' or 'e' or 'E'
                || (_index > numberStart && _source[_index] is '+' or '-'
                    && _source[_index - 1] is 'e' or 'E'))) _index++;
            if (numberStart == _index || !double.TryParse(_source[numberStart.._index],
                    NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                || !double.IsFinite(number))
                throw Error("A FormCalc numeric value is invalid or unsupported.");
            return number;
        }

        private double Function(string name)
        {
            _index++;
            var arguments = new List<double>();
            WhiteSpace();
            if (!Take(')'))
            {
                while (true)
                {
                    arguments.Add(Expression());
                    WhiteSpace();
                    if (Take(')')) break;
                    if (!Take(',')) throw Error("A FormCalc function argument list is invalid.");
                }
            }
            if (name.Equals("Abs", StringComparison.OrdinalIgnoreCase) && arguments.Count == 1)
                return Math.Abs(arguments[0]);
            if (name.Equals("Round", StringComparison.OrdinalIgnoreCase)
                && arguments.Count is 1 or 2)
            {
                int digits = arguments.Count == 1 ? 0
                    : arguments[1] == Math.Truncate(arguments[1])
                        && arguments[1] is >= 0 and <= 15
                        ? (int)arguments[1]
                        : throw Error("FormCalc Round precision must be an integer from zero through 15.");
                return Math.Round(arguments[0], digits, MidpointRounding.AwayFromZero);
            }
            if (arguments.Count > 0 && name.Equals("Sum", StringComparison.OrdinalIgnoreCase))
                return Checked(arguments.Sum());
            if (arguments.Count > 0 && name.Equals("Avg", StringComparison.OrdinalIgnoreCase))
                return Checked(arguments.Average());
            if (arguments.Count > 0 && name.Equals("Min", StringComparison.OrdinalIgnoreCase))
                return arguments.Min();
            if (arguments.Count > 0 && name.Equals("Max", StringComparison.OrdinalIgnoreCase))
                return arguments.Max();
            throw Error($"FormCalc function '{name}' is unsupported or has the wrong argument count.");
        }

        private void Enter()
        {
            if (++_depth > 64)
                throw Error("The FormCalc expression exceeds the supported nesting depth.");
        }

        private static double Checked(double value) => double.IsFinite(value)
            ? value : throw new InvalidOperationException("The FormCalc result is not finite.");
        private static bool Truth(double value) => value != 0;
        private bool Peek(string value) =>
            _source.AsSpan(_index).StartsWith(value, StringComparison.Ordinal);
        private bool Take(string value)
        {
            if (!Peek(value)) return false;
            _index += value.Length;
            return true;
        }
        private bool TakeKeyword(string value)
        {
            if (!_source.AsSpan(_index).StartsWith(value, StringComparison.OrdinalIgnoreCase))
                return false;
            int end = _index + value.Length;
            if (end < _source.Length && (char.IsAsciiLetterOrDigit(_source[end])
                || _source[end] is '_' or '$' or '.')) return false;
            _index = end;
            return true;
        }
        private bool Take(char value)
        {
            if (_index >= _source.Length || _source[_index] != value) return false;
            _index++;
            return true;
        }
        private void WhiteSpace()
        {
            while (_index < _source.Length && char.IsWhiteSpace(_source[_index])) _index++;
        }
        private FormatException Error(string message) =>
            new($"{message} Offset {_index.ToString(CultureInfo.InvariantCulture)}.");
    }
}
