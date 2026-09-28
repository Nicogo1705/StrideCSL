using System;

namespace Csl.Cpu;

/// <summary>
/// A numeric macro's value on the CPU (<c>Sdsl.Macro("ThreadNumberX")</c>), typed dynamic in shader
/// code: it converts to whatever HLSL would convert it to (a uint stream, a float), and computes as
/// an int with ints, as a float as soon as a float is involved.
/// </summary>
[System.Diagnostics.DebuggerNonUserCode]
public readonly struct MacroValue
{
    private readonly double value;
    private readonly bool isInteger;

    public MacroValue(int value) { this.value = value; isInteger = true; }
    public MacroValue(double value) { this.value = value; isInteger = false; }

    private static MacroValue Of(double value, bool integer) => integer ? new MacroValue((int)value) : new MacroValue(value);

    public static implicit operator MacroValue(int v) => new MacroValue(v);
    public static implicit operator MacroValue(uint v) => new MacroValue((int)v);
    public static implicit operator MacroValue(float v) => new MacroValue(v);
    public static implicit operator MacroValue(double v) => new MacroValue(v);

    public static implicit operator int(MacroValue m) => (int)m.value;
    public static implicit operator uint(MacroValue m) => unchecked((uint)(long)m.value);
    public static implicit operator float(MacroValue m) => (float)m.value;
    public static implicit operator double(MacroValue m) => m.value;
    public static implicit operator bool(MacroValue m) => m.value != 0;

    public static MacroValue operator +(MacroValue a, MacroValue b) => Of(a.value + b.value, a.isInteger && b.isInteger);
    public static MacroValue operator -(MacroValue a, MacroValue b) => Of(a.value - b.value, a.isInteger && b.isInteger);
    public static MacroValue operator *(MacroValue a, MacroValue b) => Of(a.value * b.value, a.isInteger && b.isInteger);
    public static MacroValue operator /(MacroValue a, MacroValue b) => a.isInteger && b.isInteger ? new MacroValue(b.value == 0 ? -1 : (int)a.value / (int)b.value) : new MacroValue(a.value / b.value);
    public static MacroValue operator %(MacroValue a, MacroValue b) => a.isInteger && b.isInteger ? new MacroValue(b.value == 0 ? -1 : (int)a.value % (int)b.value) : new MacroValue(a.value % b.value);
    public static MacroValue operator -(MacroValue a) => Of(-a.value, a.isInteger);
    public static bool operator <(MacroValue a, MacroValue b) => a.value < b.value;
    public static bool operator >(MacroValue a, MacroValue b) => a.value > b.value;
    public static bool operator <=(MacroValue a, MacroValue b) => a.value <= b.value;
    public static bool operator >=(MacroValue a, MacroValue b) => a.value >= b.value;
    public static bool operator ==(MacroValue a, MacroValue b) => a.value == b.value;
    public static bool operator !=(MacroValue a, MacroValue b) => a.value != b.value;

    public override bool Equals(object? obj) => obj is MacroValue other && other.value == value;
    public override int GetHashCode() => value.GetHashCode();
    public override string ToString() => isInteger ? ((int)value).ToString(System.Globalization.CultureInfo.InvariantCulture) : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
