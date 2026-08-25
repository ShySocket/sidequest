// Minimal NUnit.Framework shim covering exactly the assertion surface the
// Sidequest EditMode tests use, so they can run on the bundled .NET runtime.
using System;

namespace NUnit.Framework
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class TestAttribute : Attribute
    {
    }

    public sealed class AssertionException : Exception
    {
        public AssertionException(string message) : base(message)
        {
        }
    }

    public static class TestContext
    {
        public static void WriteLine(string message) => Console.WriteLine(message);
    }

    public abstract class Constraint
    {
        public abstract bool Test(object actual);
        public abstract string Description { get; }
        public ConstraintJoiner And => new ConstraintJoiner(this);
    }

    public sealed class ConstraintJoiner
    {
        private readonly Constraint left;

        public ConstraintJoiner(Constraint left)
        {
            this.left = left;
        }

        public Constraint LessThan(object expected)
            => new AndConstraint(left, new ComparisonConstraint(expected, -1, -1, "less than"));

        public Constraint GreaterThan(object expected)
            => new AndConstraint(left, new ComparisonConstraint(expected, 1, 1, "greater than"));

        public Constraint LessThanOrEqualTo(object expected)
            => new AndConstraint(left, new ComparisonConstraint(expected, -1, 0, "less than or equal to"));

        public Constraint GreaterThanOrEqualTo(object expected)
            => new AndConstraint(left, new ComparisonConstraint(expected, 0, 1, "greater than or equal to"));
    }

    public sealed class AndConstraint : Constraint
    {
        private readonly Constraint left;
        private readonly Constraint right;

        public AndConstraint(Constraint left, Constraint right)
        {
            this.left = left;
            this.right = right;
        }

        public override bool Test(object actual) => left.Test(actual) && right.Test(actual);
        public override string Description => $"{left.Description} and {right.Description}";
    }

    public sealed class EqualConstraint : Constraint
    {
        private readonly object expected;
        private double tolerance;
        private bool hasTolerance;

        public EqualConstraint(object expected)
        {
            this.expected = expected;
        }

        public EqualConstraint Within(double amount)
        {
            tolerance = amount;
            hasTolerance = true;
            return this;
        }

        public override bool Test(object actual)
        {
            if (IsNumeric(actual) && IsNumeric(expected))
            {
                double actualValue = Convert.ToDouble(actual);
                double expectedValue = Convert.ToDouble(expected);
                return hasTolerance
                    ? Math.Abs(actualValue - expectedValue) <= tolerance
                    : actualValue == expectedValue;
            }

            return Equals(actual, expected);
        }

        public override string Description => hasTolerance
            ? $"equal to {expected} within {tolerance}"
            : $"equal to {expected}";

        private static bool IsNumeric(object value)
            => value is float || value is double || value is int || value is long
                || value is short || value is byte || value is decimal
                || value is uint || value is ulong || value is ushort || value is sbyte;
    }

    public sealed class ComparisonConstraint : Constraint
    {
        private readonly object expected;
        private readonly int lowSign;
        private readonly int highSign;
        private readonly string name;

        public ComparisonConstraint(object expected, int lowSign, int highSign, string name)
        {
            this.expected = expected;
            this.lowSign = lowSign;
            this.highSign = highSign;
            this.name = name;
        }

        public override bool Test(object actual)
        {
            int comparison = Math.Sign(
                Convert.ToDouble(actual).CompareTo(Convert.ToDouble(expected)));
            return comparison == lowSign || comparison == highSign;
        }

        public override string Description => $"{name} {expected}";
    }

    public sealed class BoolConstraint : Constraint
    {
        private readonly bool expected;

        public BoolConstraint(bool expected)
        {
            this.expected = expected;
        }

        public override bool Test(object actual) => actual is bool b && b == expected;
        public override string Description => expected ? "True" : "False";
    }

    public sealed class RangeConstraint : Constraint
    {
        private readonly object from;
        private readonly object to;

        public RangeConstraint(object from, object to)
        {
            this.from = from;
            this.to = to;
        }

        public override bool Test(object actual)
        {
            double value = Convert.ToDouble(actual);
            return value >= Convert.ToDouble(from) && value <= Convert.ToDouble(to);
        }

        public override string Description => $"in range [{from}, {to}]";
    }

    public static class Is
    {
        public static EqualConstraint EqualTo(object expected) => new EqualConstraint(expected);
        public static Constraint True => new BoolConstraint(true);
        public static Constraint False => new BoolConstraint(false);
        public static Constraint Zero => new EqualConstraint(0d);
        public static Constraint GreaterThan(object expected)
            => new ComparisonConstraint(expected, 1, 1, "greater than");
        public static Constraint LessThan(object expected)
            => new ComparisonConstraint(expected, -1, -1, "less than");
        public static Constraint LessThanOrEqualTo(object expected)
            => new ComparisonConstraint(expected, -1, 0, "less than or equal to");
        public static Constraint GreaterThanOrEqualTo(object expected)
            => new ComparisonConstraint(expected, 0, 1, "greater than or equal to");
        public static Constraint InRange(object from, object to) => new RangeConstraint(from, to);
    }

    public static class Assert
    {
        public static void That(object actual, Constraint constraint)
        {
            That(actual, constraint, null);
        }

        public static void That(object actual, Constraint constraint, string message)
        {
            if (!constraint.Test(actual))
            {
                string prefix = string.IsNullOrEmpty(message) ? "" : message + "\n  ";
                throw new AssertionException(
                    $"{prefix}Expected: {constraint.Description}\n  But was: {Format(actual)}");
            }
        }

        public static void That(bool condition)
        {
            if (!condition)
            {
                throw new AssertionException("Expected: True\n  But was: False");
            }
        }

        private static string Format(object actual)
            => actual == null ? "null" : actual.ToString();
    }
}
