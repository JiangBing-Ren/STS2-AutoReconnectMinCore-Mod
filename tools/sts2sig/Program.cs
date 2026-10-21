using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

// ============================================================================
// sts2sig — STS2 元数据级「类型索引 + 签名转储」工具（替代 ilspycmd -t 的猜名字痛）
//
//   背景：ilspycmd -t 要求精确全类型名；命名空间漂移 / 嵌套类型 / 跨文件类型
//   （如 LoadRunLobbyPlayer 实际在 ...Entities.Multiplayer）会「未命中」→ 只能猜。
//   本工具用 System.Reflection.Metadata 流式读元数据（不解码 IL，秒级完成），
//   把「猜全名」变成「搜关键词」：
//
//   sts2sig index [-o file]          全量类型索引（kind|可见性|全名|基类），可 grep
//   sts2sig find  <关键词...>        类型名子串匹配 → 每个命中类型完整成员签名转储
//   sts2sig member <关键词...>       成员名子串匹配（跨全部类型的方法/属性/字段/事件）
//   sts2sig ilspy <类型全名>         打印该类型的 ilspycmd 命令模板（需要方法体时用）
//
//   可选第一个参数为 dll 路径；缺省用游戏 sts2.dll。
// ============================================================================

static class Program
{
    const string DefaultDll = @"D:\Steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64\sts2.dll";
    internal const int MaxTypesPerFind = 30;
    internal const int MaxMembersPerType = 250;

    static int Main(string[] rawArgs)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* 重定向场景可能失败，忽略 */ }
        var args = rawArgs.ToList();
        string dll = DefaultDll;
        if (args.Count > 0 && (args[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || args[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
        {
            dll = args[0];
            args.RemoveAt(0);
        }
        if (args.Count == 0 || args[0] is "help" or "-h" or "--help")
        {
            Console.Out.Write(Help);
            return args.Count == 0 ? 1 : 0;
        }

        if (!File.Exists(dll))
        {
            Console.Error.WriteLine($"DLL 不存在: {dll}");
            return 2;
        }

        var cmd = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();
        try
        {
            using var fs = File.OpenRead(dll);
            using var pe = new PEReader(fs);
            var md = pe.GetMetadataReader();
            var provider = new NameProvider(md);
            var engine = new Engine(md, provider, dll);

            return cmd switch
            {
                "index" => engine.Index(rest),
                "find" => engine.Find(rest),
                "member" => engine.Member(rest),
                "ilspy" => engine.IlspyHint(rest),
                _ => Fail($"未知命令 '{cmd}'。" + Help)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"错误: {ex.GetType().Name}: {ex.Message}");
            return 3;
        }
    }

    static int Fail(string msg)
    {
        Console.Error.WriteLine(msg);
        return 1;
    }

    const string Help = """
sts2sig <dll?> <命令> [参数...]
  index  [-o file]          全量类型索引（kind|vis|fullname|base），可 grep
  find   <kw...>            类型名子串匹配 → 每命中类型的完整成员签名
  member <kw...>            成员名子串匹配（跨所有类型）
  ilspy  <TypeFullName>     打印 ilspycmd 反编译命令模板（需要方法体时用）
示例:
  sts2sig find LoadRunLobby
  sts2sig member Rejoin
  sts2sig index -o sts2_types.txt
""";
}

sealed class GenCtx
{
    public string[] TypeParams = Array.Empty<string>();
    public string[] MethodParams = Array.Empty<string>();
}

sealed class NameProvider : ISignatureTypeProvider<string, GenCtx>
{
    private readonly MetadataReader _md;
    public NameProvider(MetadataReader md) => _md = md;

    public string GetArrayType(string e, ArrayShape s) => e + "[" + new string(',', Math.Max(0, s.Rank - 1)) + "]";
    public string GetByReferenceType(string e) => e + "&";
    public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
    public string GetGenericInstantiation(string g, ImmutableArray<string> t) => g + "<" + string.Join(", ", t) + ">";
    public string GetGenericMethodParameter(GenCtx c, int i) => i < c.MethodParams.Length ? c.MethodParams[i] : "!!M" + i;
    public string GetGenericTypeParameter(GenCtx c, int i) => i < c.TypeParams.Length ? c.TypeParams[i] : "!!T" + i;
    public string GetModifiedType(string m, string u, bool req) => u;
    public string GetPinnedType(string e) => e;
    public string GetPointerType(string e) => e + "*";
    public string GetPrimitiveType(PrimitiveTypeCode c) => c switch
    {
        PrimitiveTypeCode.Void => "void",
        PrimitiveTypeCode.Boolean => "bool",
        PrimitiveTypeCode.Char => "char",
        PrimitiveTypeCode.SByte => "sbyte",
        PrimitiveTypeCode.Byte => "byte",
        PrimitiveTypeCode.Int16 => "short",
        PrimitiveTypeCode.UInt16 => "ushort",
        PrimitiveTypeCode.Int32 => "int",
        PrimitiveTypeCode.UInt32 => "uint",
        PrimitiveTypeCode.Int64 => "long",
        PrimitiveTypeCode.UInt64 => "ulong",
        PrimitiveTypeCode.Single => "float",
        PrimitiveTypeCode.Double => "double",
        PrimitiveTypeCode.String => "string",
        PrimitiveTypeCode.Object => "object",
        PrimitiveTypeCode.IntPtr => "nint",
        PrimitiveTypeCode.UIntPtr => "nuint",
        PrimitiveTypeCode.TypedReference => "typedref",
        _ => c.ToString()
    };
    public string GetSZArrayType(string e) => e + "[]";
    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte raw) => FullName(r.GetTypeDefinition(h));
    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte raw)
    {
        var tr = r.GetTypeReference(h);
        var ns = r.GetString(tr.Namespace);
        var n = r.GetString(tr.Name);
        return string.IsNullOrEmpty(ns) ? n : ns + "." + n;
    }
    public string GetTypeFromSpecification(MetadataReader r, GenCtx c, TypeSpecificationHandle h, byte raw) =>
        r.GetTypeSpecification(h).DecodeSignature(this, c);

    public string ResolveEntity(EntityHandle h, GenCtx c)
    {
        switch (h.Kind)
        {
            case HandleKind.TypeDefinition: return FullName(_md.GetTypeDefinition((TypeDefinitionHandle)h));
            case HandleKind.TypeReference: return GetTypeFromReference(_md, (TypeReferenceHandle)h, 0);
            case HandleKind.TypeSpecification: return _md.GetTypeSpecification((TypeSpecificationHandle)h).DecodeSignature(this, c);
            default: return "?";
        }
    }

    public string FullName(TypeDefinition td)
    {
        var sb = new StringBuilder();
        Append(td, sb);
        return sb.ToString();
    }

    private void Append(TypeDefinition td, StringBuilder sb)
    {
        if (td.GetDeclaringType() is { IsNil: false } parent)
        {
            Append(_md.GetTypeDefinition(parent), sb);
            sb.Append('+');
        }
        else
        {
            var ns = _md.GetString(td.Namespace);
            if (!string.IsNullOrEmpty(ns)) sb.Append(ns).Append('.');
        }
        sb.Append(_md.GetString(td.Name));
    }

    public string BaseName(TypeDefinition td)
    {
        var b = td.BaseType;
        if (b.IsNil) return "-";
        switch (b.Kind)
        {
            case HandleKind.TypeDefinition: return FullName(_md.GetTypeDefinition((TypeDefinitionHandle)b));
            case HandleKind.TypeReference: return GetTypeFromReference(_md, (TypeReferenceHandle)b, 0);
            case HandleKind.TypeSpecification: return _md.GetTypeSpecification((TypeSpecificationHandle)b).DecodeSignature(this, new GenCtx());
            default: return b.Kind.ToString();
        }
    }
}

sealed class Engine
{
    private readonly MetadataReader _md;
    private readonly NameProvider _np;
    private readonly string _dll;

    public Engine(MetadataReader md, NameProvider np, string dll)
    {
        _md = md;
        _np = np;
        _dll = dll;
    }

    private string Kind(TypeDefinition td)
    {
        if ((td.Attributes & TypeAttributes.Interface) != 0) return "interface";
        var b = td.BaseType;
        if (!b.IsNil && b.Kind == HandleKind.TypeReference)
        {
            var tr = _md.GetTypeReference((TypeReferenceHandle)b);
            if (_md.GetString(tr.Namespace) == "System")
            {
                var bn = _md.GetString(tr.Name);
                if (bn == "Enum") return "enum";
                if (bn == "MulticastDelegate") return "delegate";
                if (bn == "ValueType") return "struct";
            }
        }
        return "class";
    }

    private static string Vis(TypeDefinition td)
    {
        var v = td.Attributes & TypeAttributes.VisibilityMask;
        return v switch
        {
            TypeAttributes.Public => "public",
            TypeAttributes.NotPublic => "internal",
            TypeAttributes.NestedPublic => "nestpub",
            TypeAttributes.NestedPrivate => "nestpriv",
            TypeAttributes.NestedFamily => "nestprot",
            TypeAttributes.NestedAssembly => "nestint",
            _ => "nestother"
        };
    }

    // ------------------------------------------------------------------ index
    public int Index(string[] args)
    {
        string? outFile = null;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "-o") outFile = args[i + 1];

        if (outFile != null)
        {
            using var w = new StreamWriter(outFile, false, Encoding.UTF8);
            WriteIndex(w);
            Console.Out.WriteLine($"索引已写入 {outFile}（{_md.TypeDefinitions.Count:N0} 个类型）。之后可直接 grep。");
        }
        else
        {
            WriteIndex(Console.Out);
        }
        return 0;
    }

    private void WriteIndex(TextWriter w)
    {
        w.WriteLine($"# sts2 type index | dll={Path.GetFileName(_dll)} | types={_md.TypeDefinitions.Count}");
        foreach (var h in _md.TypeDefinitions)
        {
            var td = _md.GetTypeDefinition(h);
            w.WriteLine($"{Kind(td)}\t{Vis(td)}\t{_np.FullName(td)}\t{_np.BaseName(td)}");
        }
    }

    // ------------------------------------------------------------------- find
    public int Find(string[] kws)
    {
        if (kws.Length == 0) return Fail("find 需要关键词。");
        var matches = new List<TypeDefinition>();
        foreach (var h in _md.TypeDefinitions)
        {
            var td = _md.GetTypeDefinition(h);
            var full = _np.FullName(td);
            if (kws.All(k => full.Contains(k, StringComparison.OrdinalIgnoreCase)))
                matches.Add(td);
        }
        if (matches.Count == 0)
        {
            Console.Out.WriteLine($"未命中：{string.Join(" ", kws)}。试试 `sts2sig member <kw>` 或先 `index` 再 grep。");
            return 0;
        }
        Console.Out.WriteLine($"# 命中 {matches.Count} 个类型（显示前 {Math.Min(matches.Count, Program.MaxTypesPerFind)} 个）");
        foreach (var td in matches.Take(Program.MaxTypesPerFind))
            DumpType(td);
        if (matches.Count > Program.MaxTypesPerFind)
            Console.Out.WriteLine($"# …其余 {matches.Count - Program.MaxTypesPerFind} 个类型未显示，请用更精确的关键词。");
        return 0;
    }

    private static int Fail(string m)
    {
        Console.Error.WriteLine(m);
        return 1;
    }

    public void DumpType(TypeDefinition td)
    {
        var full = _np.FullName(td);
        Console.Out.WriteLine();
        Console.Out.WriteLine($"=== {full}  [{Kind(td)}/{Vis(td)}]");
        var b = _np.BaseName(td);
        if (b != "-" && b != "System.Object") Console.Out.WriteLine($"  base: {b}");
        var typeParams = td.GetGenericParameters().Select(p => _md.GetString(_md.GetGenericParameter(p).Name)).ToArray();
        var tctx = new GenCtx { TypeParams = typeParams };
        foreach (var ih in td.GetInterfaceImplementations())
        {
            var iname = _np.ResolveEntity(_md.GetInterfaceImplementation(ih).Interface, tctx);
            Console.Out.WriteLine($"  iface: {iname}");
        }

        // 属性 / 事件的访问器要跳过（避免方法列表里出现 get_/set_/add_/remove_）
        var accessors = new HashSet<MethodDefinitionHandle>();
        foreach (var ph in td.GetProperties())
        {
            var p = _md.GetPropertyDefinition(ph);
            if (!p.GetAccessors().Getter.IsNil) accessors.Add(p.GetAccessors().Getter);
            if (!p.GetAccessors().Setter.IsNil) accessors.Add(p.GetAccessors().Setter);
        }
        foreach (var eh in td.GetEvents())
        {
            var e = _md.GetEventDefinition(eh);
            var eacc = e.GetAccessors();
            if (!eacc.Adder.IsNil) accessors.Add(eacc.Adder);
            if (!eacc.Remover.IsNil) accessors.Add(eacc.Remover);
        }

        int shown = 0;
        void Budget()
        {
            if (++shown > Program.MaxMembersPerType)
            {
                Console.Out.WriteLine($"  …（超过 {Program.MaxMembersPerType} 个成员，已截断；用 ilspy 命令看全量）");
            }
        }

        foreach (var fh in td.GetFields())
        {
            Budget(); if (shown > Program.MaxMembersPerType) break;
            var f = _md.GetFieldDefinition(fh);
            var type = f.DecodeSignature(_np, tctx);
            var fv = f.Attributes & FieldAttributes.FieldAccessMask;
            var vis = fv switch { FieldAttributes.Public => "pub", FieldAttributes.Family => "prot", FieldAttributes.Assembly => "int", FieldAttributes.FamORAssem => "protint", _ => "priv" };
            var stat = (f.Attributes & FieldAttributes.Static) != 0 ? " static" : "";
            var ro = (f.Attributes & FieldAttributes.InitOnly) != 0 ? " readonly" : "";
            Console.Out.WriteLine($"  [F {vis}{stat}{ro}] {type} {_md.GetString(f.Name)}");
        }

        foreach (var mh in td.GetMethods())
        {
            Budget(); if (shown > Program.MaxMembersPerType) break;
            if (accessors.Contains(mh)) continue;
            var m = _md.GetMethodDefinition(mh);
            var mctx = new GenCtx { TypeParams = typeParams, MethodParams = m.GetGenericParameters().Select(g => _md.GetString(_md.GetGenericParameter(g).Name)).ToArray() };
            var sig = m.DecodeSignature(_np, mctx);
            var va = m.Attributes & MethodAttributes.MemberAccessMask;
            var vis = va switch { MethodAttributes.Public => "pub", MethodAttributes.Family => "prot", MethodAttributes.Assembly => "int", MethodAttributes.FamORAssem => "protint", _ => "priv" };
            var stat = (m.Attributes & MethodAttributes.Static) != 0 ? " static" : "";
            var abst = (m.Attributes & MethodAttributes.Abstract) != 0 ? " abstract" : "";
            var virt = (m.Attributes & MethodAttributes.Virtual) != 0 && abst.Length == 0 ? " virtual" : "";
            var gen = mctx.MethodParams.Length > 0 ? "<" + string.Join(", ", mctx.MethodParams) + ">" : "";
            var ps = string.Join(", ", sig.ParameterTypes.Select((t, i) => $"{t} p{i}"));
            var nm = _md.GetString(m.Name);
            var name = nm == ".ctor" ? ".ctor" : nm;
            var ret = nm == ".ctor" ? "" : sig.ReturnType + " ";
            Console.Out.WriteLine($"  [M {vis}{stat}{virt}{abst}] {ret}{name}{gen}({ps})");
        }

        foreach (var ph in td.GetProperties())
        {
            Budget(); if (shown > Program.MaxMembersPerType) break;
            var p = _md.GetPropertyDefinition(ph);
            var sig = p.DecodeSignature(_np, tctx);
            var acc = string.Join("/", new[]
            {
                p.GetAccessors().Getter.IsNil ? null : "get",
                p.GetAccessors().Setter.IsNil ? null : "set"
            }.Where(x => x != null));
            Console.Out.WriteLine($"  [P {acc}] {sig.ReturnType} {_md.GetString(p.Name)}");
        }

        foreach (var eh in td.GetEvents())
        {
            Budget(); if (shown > Program.MaxMembersPerType) break;
            var e = _md.GetEventDefinition(eh);
            var handler = _np.ResolveEntity(e.Type, tctx);
            Console.Out.WriteLine($"  [E add/remove] {handler} {_md.GetString(e.Name)}");
        }

        Console.Out.WriteLine($"  # ilspy: sts2sig ilspy \"{full}\"");
    }

    // ----------------------------------------------------------------- member
    public int Member(string[] kws)
    {
        if (kws.Length == 0) return Fail("member 需要关键词。");
        int hits = 0;
        foreach (var h in _md.TypeDefinitions)
        {
            var td = _md.GetTypeDefinition(h);
            var full = _np.FullName(td);
            var tctx = new GenCtx { TypeParams = td.GetGenericParameters().Select(p => _md.GetString(_md.GetGenericParameter(p).Name)).ToArray() };

            foreach (var mh in td.GetMethods())
            {
                var m = _md.GetMethodDefinition(mh);
                var mnm = _md.GetString(m.Name);
                if (!kws.All(k => mnm.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;
                var mctx = new GenCtx { TypeParams = tctx.TypeParams, MethodParams = m.GetGenericParameters().Select(g => _md.GetString(_md.GetGenericParameter(g).Name)).ToArray() };
                var sig = m.DecodeSignature(_np, mctx);
                var ps = string.Join(", ", sig.ParameterTypes);
                var gen = mctx.MethodParams.Length > 0 ? "<" + string.Join(", ", mctx.MethodParams) + ">" : "";
                Console.Out.WriteLine($"{full}.{mnm}{gen}({ps}) -> {sig.ReturnType}");
                hits++;
            }
            foreach (var fh in td.GetFields())
            {
                var f = _md.GetFieldDefinition(fh);
                var fnm = _md.GetString(f.Name);
                if (!kws.All(k => fnm.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;
                Console.Out.WriteLine($"{full}.{fnm} : {f.DecodeSignature(_np, tctx)}  [field]");
                hits++;
            }
            foreach (var ph in td.GetProperties())
            {
                var p = _md.GetPropertyDefinition(ph);
                var pnm = _md.GetString(p.Name);
                if (!kws.All(k => pnm.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;
                Console.Out.WriteLine($"{full}.{pnm} : {p.DecodeSignature(_np, tctx).ReturnType}  [prop]");
                hits++;
            }
            foreach (var eh in td.GetEvents())
            {
                var e = _md.GetEventDefinition(eh);
                var enm = _md.GetString(e.Name);
                if (!kws.All(k => enm.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;
                Console.Out.WriteLine($"{full}.{enm} : {_np.ResolveEntity(e.Type, tctx)}  [event]");
                hits++;
            }
            if (hits > 400)
            {
                Console.Out.WriteLine("# 命中超过 400 条，已停止。请用更精确的关键词。");
                break;
            }
        }
        Console.Out.WriteLine($"# 共 {hits} 条成员命中。");
        return 0;
    }

    // ---------------------------------------------------------------- ilspy
    public int IlspyHint(string[] args)
    {
        if (args.Length == 0) return Fail("ilspy 需要类型全名。");
        var full = string.Join(" ", args);
        Console.Out.WriteLine($"""
# 整个类型（方法体）:
ilspycmd -t "{full}" "{_dll}"

# 单个成员（11.1+ 支持，避免刷屏）:
ilspycmd -m "M:{full}.<方法名>(<参数类型…>)" "{_dll}"
ilspycmd -m "P:{full}.<属性名>" "{_dll}"

# 注意：路径含空格时给 DLL 路径加引号；-t/-m 都要精确全名（先用 find 查全名）。
""");
        return 0;
    }
}
