using System;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace AutoReconnect.Scripts;

/// <summary>
/// Utility for finding types and members by name without hard assembly references.
/// Pattern used extensively in CDC's CharacterDetector.
///
/// v0.9.15: 新增编译委托缓存（借鉴 Rewind / GameCompat 的 Expression.Lambda 模式），
/// 把热路径上的 PropertyInfo.GetValue/SetValue 替换为编译后的强类型委托，
/// 性能提升 5-10x（Reflection 的 MethodInfo.Invoke 每次都有 boxing + 权限检查开销）。
/// </summary>
public static class ReflectionHelper
{
    /// <summary>
    /// v0.9.15: 编译后的 getter/setter 委托缓存。
    /// Key = "TypeFullName|MemberName|Kind"，Value = 编译后的委托。
    /// </summary>
    private static readonly ConcurrentDictionary<string, object> _compiledAccessors = new();

    /// <summary>
    /// Find a type by full name across all loaded assemblies.
    /// </summary>
    public static Type? FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = asm.GetType(fullName);
            if (type != null) return type;
        }
        return null;
    }

    /// <summary>
    /// Find a type by simple name suffix match.
    /// </summary>
    public static Type? FindTypeBySuffix(string suffix)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (var type in asm.GetTypes())
            {
                if (type.FullName?.EndsWith(suffix) == true)
                    return type;
            }
        }
        return null;
    }

    /// <summary>
    /// Get a property value by trying multiple possible names.
    /// </summary>
    public static object? GetPropertyValue(object obj, string[] names,
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
    {
        var type = obj.GetType();
        foreach (var name in names)
        {
            var prop = type.GetProperty(name, flags);
            if (prop != null)
                return prop.GetValue(obj);
        }
        return null;
    }

    /// <summary>
    /// Get a field value by trying multiple possible names.
    /// </summary>
    public static object? GetFieldValue(object obj, string[] names,
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
    {
        var type = obj.GetType();
        foreach (var name in names)
        {
            var field = type.GetField(name, flags);
            if (field != null)
                return field.GetValue(obj);
        }
        return null;
    }

    /// <summary>
    /// Set a field value by trying multiple possible names (used for injection into private backing fields).
    /// </summary>
    public static void SetFieldValue(object obj, string[] names, object? value,
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
    {
        var type = obj.GetType();
        foreach (var name in names)
        {
            var field = type.GetField(name, flags);
            if (field != null) { field.SetValue(obj, value); return; }
        }
        Diag.Log($"[ReflectionHelper] [SetFieldValue] none of {string.Join(",", names)} found on {type.FullName}");
    }

    /// <summary>
    /// Set a property value by trying multiple possible names (works for private setters).
    /// </summary>
    public static void SetPropertyValue(object obj, string[] names, object? value,
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
    {
        var type = obj.GetType();
        foreach (var name in names)
        {
            var prop = type.GetProperty(name, flags);
            if (prop != null) { prop.SetValue(obj, value); return; }
        }
        Diag.Log($"[ReflectionHelper] [SetPropertyValue] none of {string.Join(",", names)} found on {type.FullName}");
    }

    /// <summary>
    /// Append an item to a List-field (by trying multiple possible field names). Used for OrbQueue._orbs.
    /// </summary>
    public static void SetListElement(object obj, string[] names, object? item,
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
    {
        var type = obj.GetType();
        foreach (var name in names)
        {
            var field = type.GetField(name, flags);
            if (field != null)
            {
                if (field.GetValue(obj) is System.Collections.IList list)
                    list.Add(item);
                else
                    Diag.Log($"[ReflectionHelper] [SetListElement] field {name} on {type.FullName} is not an IList");
                return;
            }
        }
        Diag.Log($"[ReflectionHelper] [SetListElement] none of {string.Join(",", names)} found on {type.FullName}");
    }

    /// <summary>
    /// Invoke a method by trying multiple possible names.
    /// </summary>
    public static object? InvokeMethod(object obj, string[] names,
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
        object?[]? args = null)
    {
        args ??= Array.Empty<object?>();
        var type = obj.GetType();
        foreach (var name in names)
        {
            var method = type.GetMethod(name, flags);
            if (method != null)
                return method.Invoke(obj, args);
        }
        return null;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  v0.9.15: 编译委托缓存（借鉴 Rewind GameCompat 的 Expression.Lambda）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// v0.9.15: 编译属性/字段 getter 为 Func&lt;object, object?&gt; 并缓存。
    /// 每次 GetValue 调用约 5-10x 快于 PropertyInfo.GetValue。
    /// </summary>
    public static Func<object, object?> GetOrCreateCompiledGetter(Type type, string memberName,
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
    {
        var cacheKey = $"{type.FullName}|{memberName}|getter";
        if (_compiledAccessors.TryGetValue(cacheKey, out var cached) && cached is Func<object, object?> getter)
            return getter;

        var compiled = BuildCompiledGetter(type, memberName, flags);
        _compiledAccessors[cacheKey] = compiled;
        return compiled;
    }

    /// <summary>
    /// v0.9.15: 编译属性/字段 setter 为 Action&lt;object, object?&gt; 并缓存。
    /// 每次 SetValue 调用约 5-10x 快于 PropertyInfo.SetValue。
    /// </summary>
    public static Action<object, object?> GetOrCreateCompiledSetter(Type type, string memberName,
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
    {
        var cacheKey = $"{type.FullName}|{memberName}|setter";
        if (_compiledAccessors.TryGetValue(cacheKey, out var cached) && cached is Action<object, object?> setter)
            return setter;

        var compiled = BuildCompiledSetter(type, memberName, flags);
        _compiledAccessors[cacheKey] = compiled;
        return compiled;
    }

    private static Func<object, object?> BuildCompiledGetter(Type type, string memberName, BindingFlags flags)
    {
        // 优先找 Property
        var prop = type.GetProperty(memberName, flags);
        if (prop != null && prop.CanRead)
        {
            var objParam = Expression.Parameter(typeof(object), "obj");
            var castObj = Expression.Convert(objParam, type);
            var get = Expression.Property(castObj, prop);
            var castResult = Expression.Convert(get, typeof(object));
            return Expression.Lambda<Func<object, object?>>(castResult, objParam).Compile();
        }

        // 其次找 Field
        var field = type.GetField(memberName, flags);
        if (field != null)
        {
            var objParam = Expression.Parameter(typeof(object), "obj");
            var castObj = Expression.Convert(objParam, type);
            var get = Expression.Field(castObj, field);
            var castResult = Expression.Convert(get, typeof(object));
            return Expression.Lambda<Func<object, object?>>(castResult, objParam).Compile();
        }

        throw new InvalidOperationException(
            $"No readable member '{memberName}' found on {type.FullName}");
    }

    private static Action<object, object?> BuildCompiledSetter(Type type, string memberName, BindingFlags flags)
    {
        // 优先找 Property
        var prop = type.GetProperty(memberName, flags);
        if (prop != null && prop.CanWrite)
        {
            var objParam = Expression.Parameter(typeof(object), "obj");
            var valParam = Expression.Parameter(typeof(object), "val");
            var castObj = Expression.Convert(objParam, type);
            var castVal = Expression.Convert(valParam, prop.PropertyType);
            var set = Expression.Assign(Expression.Property(castObj, prop), castVal);
            return Expression.Lambda<Action<object, object?>>(set, objParam, valParam).Compile();
        }

        // 其次找 Field
        var field = type.GetField(memberName, flags);
        if (field != null)
        {
            var objParam = Expression.Parameter(typeof(object), "obj");
            var valParam = Expression.Parameter(typeof(object), "val");
            var castObj = Expression.Convert(objParam, type);
            var castVal = Expression.Convert(valParam, field.FieldType);
            var set = Expression.Assign(Expression.Field(castObj, field), castVal);
            return Expression.Lambda<Action<object, object?>>(set, objParam, valParam).Compile();
        }

        throw new InvalidOperationException(
            $"No writable member '{memberName}' found on {type.FullName}");
    }
}
