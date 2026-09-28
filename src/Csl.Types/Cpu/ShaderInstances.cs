using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Csl.Types;

namespace Csl.Cpu;

/// <summary>
/// Shader classes as objects the CPU runs: an abstract one gets a subclass made at run time whose
/// abstract methods throw (nothing in the run implements them); the samplers get the description of
/// their [Sampler] attribute; the members the [Mixin] stubs declare get the mixin's initial values.
/// </summary>
[System.Diagnostics.DebuggerNonUserCode]
public static class ShaderInstances
{
    private static readonly ConcurrentDictionary<Type, Type> Concrete = new ConcurrentDictionary<Type, Type>();
    private static readonly ConcurrentDictionary<Type, object> Prototypes = new ConcurrentDictionary<Type, object>();
    private static readonly Func<object, object> CloneMethod = (Func<object, object>)typeof(object)
        .GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate(typeof(Func<object, object>));
    private static ModuleBuilder? module;

    /// <summary>A new instance of the shader, ready to run: a copy of the prototype of its type.</summary>
    public static object Create(Type shader) => Clone(Prototypes.GetOrAdd(shader, MakePrototype));

    public static T Create<T>() where T : class => (T)Create(typeof(T));

    /// <summary>A shallow copy: what each lane of a run gets of the configured shader.</summary>
    public static object Clone(object instance) => CloneMethod(instance);

    private static object MakePrototype(Type shader)
    {
        var instance = Activator.CreateInstance(ConcreteType(shader), nonPublic: true)!;
        // The stubs of the mixins' members start as the mixins' own do (their initializers).
        foreach (var mixin in MixinGraph(shader))
            if (mixin != shader && !mixin.IsAssignableFrom(shader))
                Members.CopyDefaultsInto(Create(mixin), instance);
        InitializeSamplers(instance, shader);
        return instance;
    }

    /// <summary>The shader's type, or a subclass whose abstract members throw when the shader is abstract.</summary>
    public static Type ConcreteType(Type shader)
    {
        if (!shader.IsAbstract)
            return shader;
        return Concrete.GetOrAdd(shader, EmitConcrete);
    }

    private static Type EmitConcrete(Type shader)
    {
        lock (Concrete)
        {
            module ??= AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Csl.Cpu.Instances"), AssemblyBuilderAccess.Run).DefineDynamicModule("Csl.Cpu.Instances");
            var builder = module.DefineType("Cpu_" + shader.FullName!.Replace('.', '_').Replace('`', '_') + "_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class, shader);
            var notImplemented = typeof(NotSupportedException).GetConstructor(new[] { typeof(string) })!;
            foreach (var method in AbstractMethods(shader))
            {
                var parameters = method.GetParameters();
                var overriding = builder.DefineMethod(method.Name,
                    (method.Attributes & ~(MethodAttributes.Abstract | MethodAttributes.NewSlot)) | MethodAttributes.Virtual | MethodAttributes.HideBySig,
                    method.ReturnType, parameters.Select(p => p.ParameterType).ToArray());
                var il = overriding.GetILGenerator();
                il.Emit(OpCodes.Ldstr, $"{method.DeclaringType!.Name}.{method.Name} is abstract and nothing in this run implements it");
                il.Emit(OpCodes.Newobj, notImplemented);
                il.Emit(OpCodes.Throw);
                builder.DefineMethodOverride(overriding, method);
            }
            var baseConstructor = shader.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null)
                ?? throw new NotSupportedException(shader.Name + " has no parameterless constructor");
            var constructor = builder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
            var cil = constructor.GetILGenerator();
            cil.Emit(OpCodes.Ldarg_0);
            cil.Emit(OpCodes.Call, baseConstructor);
            cil.Emit(OpCodes.Ret);
            return builder.CreateType()!;
        }
    }

    private static IEnumerable<MethodInfo> AbstractMethods(Type type)
    {
        var implemented = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<MethodInfo>();
        for (var current = type; current != null && current != typeof(object); current = current.BaseType)
        {
            foreach (var method in current.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                var key = method.Name + "(" + string.Join(",", method.GetParameters().Select(p => p.ParameterType.FullName)) + ")";
                if (method.IsAbstract && !implemented.Contains(key))
                    result.Add(method);
                implemented.Add(key);
            }
        }
        return result;
    }

    /// <summary>Every [Mixin] shader the type reaches, through its bases and the mixins' own mixins.</summary>
    public static List<Type> MixinGraph(Type shader)
    {
        var result = new List<Type>();
        var seen = new HashSet<Type>();
        void Visit(Type type)
        {
            for (var current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                if (!seen.Add(current))
                    continue;
                result.Add(current);
                foreach (var attribute in current.GetCustomAttributes<MixinAttribute>(inherit: false))
                    foreach (var mixin in attribute.Shaders)
                        Visit(mixin);
            }
        }
        Visit(shader);
        return result;
    }

    /// <summary>
    /// A SamplerState without a description gets the one of its [Sampler] attribute, on the field or on
    /// the mixin field it is the stub of (SpriteBase's PointSampler is Texturing's).
    /// </summary>
    private static void InitializeSamplers(object instance, Type shader)
    {
        var graph = MixinGraph(shader);
        foreach (var field in Members.InstanceFields(instance.GetType()).Values)
        {
            if (field.FieldType != typeof(SamplerState) && field.FieldType != typeof(SamplerComparisonState))
                continue;
            var attribute = field.GetCustomAttribute<SamplerAttribute>()
                ?? graph.Select(t => t.GetField(field.Name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)?.GetCustomAttribute<SamplerAttribute>()).FirstOrDefault(a => a != null);
            var description = attribute != null ? SamplerDescription.From(attribute) : SamplerDescription.Default;
            field.SetValue(instance, field.FieldType == typeof(SamplerState) ? new SamplerState(description) : (object)new SamplerComparisonState(description));
        }
    }
}

/// <summary>
/// A [Mixin]'s members for a C# shader run on the CPU. In SDSL a mixin's members become the shader's
/// own; in C# they are stubs on the class and the real ones on the mixin's. A call through a stub runs
/// on an instance of the mixin kept for the shader object: before, its members take the values of the
/// shader's members of the same name (parameters, streams); after, the shader takes back what it wrote.
/// </summary>
[System.Diagnostics.DebuggerNonUserCode]
public static class Mixins
{
    private static readonly ConditionalWeakTable<object, Dictionary<Type, object>> Instances = new ConditionalWeakTable<object, Dictionary<Type, object>>();

    /// <summary>The mixin's instance for this shader, its members set from the shader's.</summary>
    public static T Enter<T>(object host) where T : class
    {
        var mixin = Get<T>(host);
        Members.Copy(host, mixin);
        return mixin;
    }

    /// <summary>After the call: what the mixin wrote goes back to the shader.</summary>
    public static void Leave(object host, object mixin) => Members.Copy(mixin, host);

    private static T Get<T>(object host) where T : class
    {
        var map = Instances.GetOrCreateValue(host);
        lock (map)
        {
            if (!map.TryGetValue(typeof(T), out var instance))
                map[typeof(T)] = instance = ShaderInstances.Create(typeof(T));
            return (T)instance;
        }
    }

    /// <summary>
    /// <c>Sdsl.Static&lt;T&gt;()</c>: a shader named to call its members, as SDSL's <c>BlendUtils.BasicBlend(…)</c>.
    /// In a run, the instance kept for the lane's shader (the effect's parameters seen); otherwise a new one.
    /// </summary>
    public static T Static<T>() where T : class
    {
        if (Run.CurrentShader is { } root)
            return Enter<T>(root);
        return ShaderInstances.Create<T>();
    }
}

/// <summary>Members by name, as SDSL merges them: fields of the same name are one member.</summary>
[System.Diagnostics.DebuggerNonUserCode]
public static class Members
{
    private static readonly ConcurrentDictionary<Type, Dictionary<string, FieldInfo>> Fields = new ConcurrentDictionary<Type, Dictionary<string, FieldInfo>>();
    private static readonly ConcurrentDictionary<(Type, Type), (FieldInfo From, FieldInfo To)[]> Pairs = new ConcurrentDictionary<(Type, Type), (FieldInfo, FieldInfo)[]>();
    private static readonly ConcurrentDictionary<(Type, Type), Action<object, object>> Copiers = new ConcurrentDictionary<(Type, Type), Action<object, object>>();
    private static readonly ConcurrentDictionary<(Type, string, Type), Delegate?> Accessors = new ConcurrentDictionary<(Type, string, Type), Delegate?>();

    /// <summary>The instance fields of a type and its bases, the most derived one for a name.</summary>
    public static Dictionary<string, FieldInfo> InstanceFields(Type type) => Fields.GetOrAdd(type, t =>
    {
        var result = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
        for (var current = t; current != null && current != typeof(object); current = current.BaseType)
            foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (!field.Name.Contains("<") && !result.ContainsKey(field.Name))
                    result[field.Name] = field;
        return result;
    });

    private static (FieldInfo From, FieldInfo To)[] PairsOf(Type from, Type to) => Pairs.GetOrAdd((from, to), key =>
    {
        var target = InstanceFields(key.Item2);
        return InstanceFields(key.Item1).Values
            .Where(f => target.TryGetValue(f.Name, out var t) && t.FieldType == f.FieldType)
            .Select(f => (f, target[f.Name]))
            .ToArray();
    });

    /// <summary>Every member both have, from one to the other: compiled once per pair of types, a mixin call makes two.</summary>
    public static void Copy(object from, object to) => Copiers.GetOrAdd((from.GetType(), to.GetType()), key =>
    {
        var source = System.Linq.Expressions.Expression.Parameter(typeof(object));
        var target = System.Linq.Expressions.Expression.Parameter(typeof(object));
        var typedSource = System.Linq.Expressions.Expression.Variable(key.Item1);
        var typedTarget = System.Linq.Expressions.Expression.Variable(key.Item2);
        var body = new List<System.Linq.Expressions.Expression>
        {
            System.Linq.Expressions.Expression.Assign(typedSource, System.Linq.Expressions.Expression.Convert(source, key.Item1)),
            System.Linq.Expressions.Expression.Assign(typedTarget, System.Linq.Expressions.Expression.Convert(target, key.Item2)),
        };
        foreach (var (f, t) in PairsOf(key.Item1, key.Item2))
            if (!t.IsInitOnly)
                body.Add(System.Linq.Expressions.Expression.Assign(System.Linq.Expressions.Expression.Field(typedTarget, t), System.Linq.Expressions.Expression.Field(typedSource, f)));
        return System.Linq.Expressions.Expression.Lambda<Action<object, object>>(
            System.Linq.Expressions.Expression.Block(new[] { typedSource, typedTarget }, body), source, target).Compile();
    })(from, to);

    /// <summary>A compiled setter of a field of this name and type, or null when the shader has none.</summary>
    public static Action<object, T>? Setter<T>(Type type, string name) => (Action<object, T>?)Accessors.GetOrAdd((type, "set " + name, typeof(T)), key =>
    {
        if (!InstanceFields(type).TryGetValue(name, out var field) || field.FieldType != typeof(T) || field.IsInitOnly)
            return null;
        var instance = System.Linq.Expressions.Expression.Parameter(typeof(object));
        var value = System.Linq.Expressions.Expression.Parameter(typeof(T));
        return System.Linq.Expressions.Expression.Lambda<Action<object, T>>(
            System.Linq.Expressions.Expression.Assign(System.Linq.Expressions.Expression.Field(System.Linq.Expressions.Expression.Convert(instance, field.DeclaringType!), field), value), instance, value).Compile();
    });

    /// <summary>A compiled getter of a field of this name and type, or null when the shader has none.</summary>
    public static Func<object, T>? Getter<T>(Type type, string name) => (Func<object, T>?)Accessors.GetOrAdd((type, "get " + name, typeof(T)), key =>
    {
        if (!InstanceFields(type).TryGetValue(name, out var field) || field.FieldType != typeof(T))
            return null;
        var instance = System.Linq.Expressions.Expression.Parameter(typeof(object));
        return System.Linq.Expressions.Expression.Lambda<Func<object, T>>(
            System.Linq.Expressions.Expression.Field(System.Linq.Expressions.Expression.Convert(instance, field.DeclaringType!), field), instance).Compile();
    });

    /// <summary>The members <paramref name="to"/> still has at their default value, from <paramref name="from"/>.</summary>
    public static void CopyDefaultsInto(object from, object to)
    {
        foreach (var (source, target) in PairsOf(from.GetType(), to.GetType()))
        {
            var current = target.GetValue(to);
            var fresh = target.FieldType.IsValueType ? Activator.CreateInstance(target.FieldType) : null;
            if (Equals(current, fresh))
                target.SetValue(to, source.GetValue(from));
        }
    }

    /// <summary><c>value.name</c> by reflection: a swizzle of a vector, a field or a property.</summary>
    public static object Get(object value, string name)
    {
        var type = value.GetType();
        var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        if (property != null)
            return property.GetValue(value)!;
        if (InstanceFields(type).TryGetValue(name, out var field))
            return field.GetValue(value)!;
        throw new MissingMemberException(type.Name, name);
    }

    public static void Set(object target, string name, object value)
    {
        if (InstanceFields(target.GetType()).TryGetValue(name, out var field))
        {
            field.SetValue(target, value);
            return;
        }
        var property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMemberException(target.GetType().Name, name);
        property.SetValue(target, value);
    }
}
