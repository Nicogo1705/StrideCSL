using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Csl.Cpu;

/// <summary>
/// HLSL's conversions between its numeric types, for the markers the conversion writes where C# wants
/// them spelled out (<c>Sdsl.Implicit&lt;float3&gt;(v)</c>, <c>Sdsl.Cast&lt;float&gt;(b)</c>): a scalar is
/// splatted, a vector or a matrix truncated, each component converted (a float to an int toward zero,
/// saturated, NaN to 0; a number to a bool by comparing with zero).
/// </summary>
public static class HlslConvert
{
    private static readonly Regex TypeName = new Regex(@"^(bool|int|uint|half|float|double)([1-4])?(x([1-4]))?$", RegexOptions.Compiled);

    private sealed class Shape
    {
        public string Scalar = "float";
        public int Rows = 1, Columns = 1;
        public bool IsMatrix;
        public FieldInfo[] Rows_ = Array.Empty<FieldInfo>();
        public FieldInfo[] Components = Array.Empty<FieldInfo>();
    }

    private static readonly ConcurrentDictionary<Type, Shape?> Shapes = new ConcurrentDictionary<Type, Shape?>();

    public static T Convert<T>(object value)
    {
        if (value is T same)
            return same;
        var from = ShapeOf(value.GetType()) ?? throw new NotSupportedException($"No HLSL conversion from {value.GetType().Name}");
        var to = ShapeOf(typeof(T)) ?? throw new NotSupportedException($"No HLSL conversion to {typeof(T).Name}");
        var components = Decompose(value, from);
        return (T)Compose(typeof(T), to, components, from);
    }

    private static Shape? ShapeOf(Type type) => Shapes.GetOrAdd(type, t =>
    {
        if (t == typeof(bool)) return new Shape { Scalar = "bool" };
        if (t == typeof(int)) return new Shape { Scalar = "int" };
        if (t == typeof(uint)) return new Shape { Scalar = "uint" };
        if (t == typeof(float)) return new Shape { Scalar = "float" };
        if (t == typeof(double)) return new Shape { Scalar = "double" };
        if (t == typeof(long)) return new Shape { Scalar = "int" };
        if (t == typeof(ulong)) return new Shape { Scalar = "uint" };
        if (t.Namespace != "Csl.Types")
            return null;
        var match = TypeName.Match(t.Name);
        if (!match.Success)
            return null;
        var shape = new Shape { Scalar = match.Groups[1].Value };
        if (t.Name == "half")
            return shape;
        if (match.Groups[4].Success)
        {
            shape.IsMatrix = true;
            shape.Rows = int.Parse(match.Groups[2].Value);
            shape.Columns = int.Parse(match.Groups[4].Value);
            shape.Rows_ = new FieldInfo[shape.Rows];
            for (int r = 0; r < shape.Rows; r++)
                shape.Rows_[r] = t.GetField("r" + r)!;
            var rowType = shape.Rows_[0].FieldType;
            shape.Components = new FieldInfo[shape.Columns];
            for (int c = 0; c < shape.Columns; c++)
                shape.Components[c] = rowType.GetField("xyzw"[c].ToString())!;
            return shape;
        }
        shape.Columns = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 1;
        shape.Components = new FieldInfo[shape.Columns];
        for (int c = 0; c < shape.Columns; c++)
            shape.Components[c] = t.GetField("xyzw"[c].ToString())!;
        return shape;
    });

    /// <summary>The components, row after row, as doubles (the bits of an int or a uint kept exactly).</summary>
    private static double[] Decompose(object value, Shape shape)
    {
        if (shape.Rows == 1 && shape.Columns == 1 && !shape.IsMatrix && shape.Components.Length == 0)
            return new[] { ScalarToDouble(value) };
        if (!shape.IsMatrix)
        {
            var result = new double[shape.Columns];
            for (int c = 0; c < shape.Columns; c++)
                result[c] = ScalarToDouble(shape.Components[c].GetValue(value)!);
            return result;
        }
        var all = new double[shape.Rows * shape.Columns];
        for (int r = 0; r < shape.Rows; r++)
        {
            var row = shape.Rows_[r].GetValue(value)!;
            for (int c = 0; c < shape.Columns; c++)
                all[r * shape.Columns + c] = ScalarToDouble(shape.Components[c].GetValue(row)!);
        }
        return all;
    }

    private static double ScalarToDouble(object value) => value switch
    {
        bool b => b ? 1 : 0,
        int i => i,
        uint u => u,
        float f => f,
        double d => d,
        long l => l,
        ulong ul => ul,
        Types.half h => (float)h,
        _ => throw new NotSupportedException("Not an HLSL scalar: " + value.GetType().Name),
    };

    private static object Compose(Type type, Shape to, double[] components, Shape from)
    {
        double At(int row, int column)
        {
            if (components.Length == 1)
                return components[0];
            if (from.IsMatrix && to.IsMatrix)
                return components[row * from.Columns + column];
            // A matrix to a vector: its components row after row.
            return components[Math.Min(row * to.Columns + column, components.Length - 1)];
        }
        if (to.Components.Length == 0)
            return Scalar(to.Scalar, type, components[0]);
        if (!to.IsMatrix)
        {
            object vector = Activator.CreateInstance(type)!;
            for (int c = 0; c < to.Columns; c++)
                to.Components[c].SetValue(vector, Scalar(to.Scalar, to.Components[c].FieldType, At(0, c)));
            return vector;
        }
        object matrix = Activator.CreateInstance(type)!;
        for (int r = 0; r < to.Rows; r++)
        {
            object row = Activator.CreateInstance(to.Rows_[r].FieldType)!;
            for (int c = 0; c < to.Columns; c++)
                to.Components[c].SetValue(row, Scalar(to.Scalar, to.Components[c].FieldType, At(r, c)));
            to.Rows_[r].SetValue(matrix, row);
        }
        return matrix;
    }

    private static object Scalar(string scalar, Type type, double value)
    {
        switch (scalar)
        {
            case "bool": return value != 0;
            case "int": return type == typeof(long) ? (object)(long)ToInt(value) : ToInt(value);
            case "uint": return type == typeof(ulong) ? (object)(ulong)ToUInt(value) : ToUInt(value);
            case "double": return value;
            case "half": return (Types.half)(float)value;
            default: return (float)value;
        }
    }

    /// <summary>Toward zero, saturated, NaN to 0; an integer out of range keeps its low 32 bits.</summary>
    private static int ToInt(double value)
    {
        if (double.IsNaN(value)) return 0;
        if (Math.Floor(value) == value && value >= uint.MinValue && value <= uint.MaxValue) return unchecked((int)(uint)value);
        return value >= int.MaxValue ? int.MaxValue : value <= int.MinValue ? int.MinValue : (int)value;
    }

    private static uint ToUInt(double value)
    {
        if (double.IsNaN(value)) return 0;
        if (Math.Floor(value) == value && value >= int.MinValue && value < 0) return unchecked((uint)(int)value);
        return value >= uint.MaxValue ? uint.MaxValue : value <= 0 ? 0 : (uint)value;
    }
}
