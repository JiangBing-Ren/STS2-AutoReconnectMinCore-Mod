using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

/// <summary>
/// 复刻游戏的 <c>MessageTypes.Initialize()</c> + <c>ContentSorter</c> + <c>NetTypeCache</c>
/// 的网络消息 ID 分配过程，用来**实证**：多个 mod 各自定义 INetMessage 时，ID 会不会撞车。
///
/// 读取的是元数据、不加载程序集，所以可以安全地把 sts2.dll 和任意第三方 mod DLL 一起扫。
///
/// 用法：
///   sts2sig netmsg <mod1.dll> <mod2.dll> ...
/// 基 DLL 是默认的 sts2.dll（也可在前面显式指定别的 dll）。
/// </summary>
static class NetMsgScanner
{
    const string INetMessage = "MegaCrit.Sts2.Core.Multiplayer.Serialization.INetMessage";

    sealed record MsgType(string Name, string FullName, string Asm, bool IsBase);

    public static int Run(string baseDll, string[] modDlls)
    {
        if (modDlls.Length == 0)
        {
            Console.Error.WriteLine("netmsg 至少要给一个 mod DLL。示例: sts2sig netmsg AutoReconnectMinCore.dll MpLib.dll");
            return 1;
        }

        var all = new List<MsgType>();
        int baseCount = 0;

        try
        {
            var baseTypes = Scan(baseDll, isBase: true);
            baseCount = baseTypes.Count;
            all.AddRange(baseTypes);

            foreach (var d in modDlls)
            {
                if (!File.Exists(d))
                {
                    Console.Error.WriteLine($"跳过（不存在）: {d}");
                    continue;
                }
                all.AddRange(Scan(d, isBase: false));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"扫描失败: {ex.GetType().Name}: {ex.Message}");
            return 3;
        }

        // —— 复刻 ContentSorter<T>.Sort（getMessageId = t.Name，affectsGameplay 此处均为 true 故省略该层） ——
        all.Sort((a, b) =>
        {
            // 基游戏类型优先（对应 ContentSorter 里 mod == null 排在前）
            if (a.IsBase != b.IsBase) return a.IsBase ? -1 : 1;

            int c = string.CompareOrdinal(a.Name, b.Name);          // id = 类型短名
            if (c != 0) return c;
            if (a.IsBase) return 0;                                  // 两边都是基游戏

            c = string.CompareOrdinal(a.Asm, b.Asm);                 // mod id 的近似（程序集名）
            if (c != 0) return c;
            c = string.CompareOrdinal(a.FullName, b.FullName);
            if (c != 0) return c;
            return string.CompareOrdinal(a.Asm, b.Asm);
        });

        // —— 复刻 NetTypeCache：按下标发号 ——
        var ids = new Dictionary<string, List<int>>();   // FullName -> 分配到的 ID（重复即冲突）
        for (int i = 0; i < all.Count; i++)
        {
            if (!ids.TryGetValue(all[i].FullName, out var l)) ids[all[i].FullName] = l = new List<int>();
            l.Add(i);
        }

        Console.WriteLine($"# 扫描完成：基游戏 {baseCount} 种 + mod {all.Count - baseCount} 种 = 共 {all.Count} 种 INetMessage");
        Console.WriteLine($"# 参与扫描的 mod DLL：{string.Join(", ", modDlls.Select(Path.GetFileName))}");
        Console.WriteLine();

        Console.WriteLine("## mod 自定义消息（排在基游戏之后）");
        // 这里必须用下标遍历：MsgType 是 record，值相等的语义会让 IndexOf 找错位置
        var modOnes = all.Select((t, i) => (t, i)).Where(x => !x.t.IsBase).ToList();
        if (modOnes.Count == 0)
        {
            Console.WriteLine("（没有扫到任何 mod 自定义 INetMessage）");
        }
        else
        {
            foreach (var (t, i) in modOnes)
                Console.WriteLine($"  id={i,4}  {t.Name,-44} [{t.Asm}]  {t.FullName}");
        }

        Console.WriteLine();
        var dup = ids.Where(kv => kv.Value.Count > 1).ToList();
        if (dup.Count == 0)
        {
            Console.WriteLine("✅ 无重复类型，ID 全表唯一 —— 各 mod 消息不会撞车。");
        }
        else
        {
            Console.WriteLine("❌ 存在重复类型（会造成 ID 冲突）:");
            foreach (var kv in dup)
                Console.WriteLine($"   {kv.Key} → ids {string.Join(",", kv.Value)}");
        }

        // 同名但不同 FullName 的情况：靠 mod id / FullName 兜底定序，本身安全，但值得点出来
        var sameName = all.GroupBy(t => t.Name).Where(g => g.Count() > 1).ToList();
        if (sameName.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"ℹ 有 {sameName.Count} 组类型短名相同（靠 mod id / FullName 继续定序，仍确定）：");
            foreach (var g in sameName)
                Console.WriteLine($"   “{g.Key}”：{string.Join(" | ", g.Select(x => $"{x.Asm}/{x.FullName}"))}");
        }

        return 0;
    }

    static List<MsgType> Scan(string dll, bool isBase)
    {
        var list = new List<MsgType>();
        using var fs = File.OpenRead(dll);
        using var pe = new PEReader(fs);
        var md = pe.GetMetadataReader();
        string asm = Path.GetFileNameWithoutExtension(dll);

        foreach (var h in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(h);

            // 复刻 ReflectionHelper.GetSubtypesFromList 的筛选：非抽象、非接口、实现/继承了目标接口
            if ((td.Attributes & TypeAttributes.Abstract) != 0) continue;
            if ((td.Attributes & TypeAttributes.Interface) != 0) continue;

            bool implements = false;
            foreach (var iiHandle in td.GetInterfaceImplementations())
            {
                var ii = md.GetInterfaceImplementation(iiHandle);
                if (InterfaceFullName(md, ii.Interface) == INetMessage)
                {
                    implements = true;
                    break;
                }
            }
            if (!implements) continue;

            list.Add(new MsgType(md.GetString(td.Name), TypeFullName(md, h), asm, isBase));
        }
        return list;
    }

    static string TypeFullName(MetadataReader md, TypeDefinitionHandle handle)
    {
        var td = md.GetTypeDefinition(handle);
        string name = md.GetString(td.Name);

        var declaring = td.GetDeclaringType();
        if (!declaring.IsNil)
            return TypeFullName(md, declaring) + "+" + name;   // 嵌套类型：Outer+Inner，与 Type.FullName 一致

        string ns = md.GetString(td.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    static string InterfaceFullName(MetadataReader md, EntityHandle h)
    {
        switch (h.Kind)
        {
            case HandleKind.TypeDefinition:
                return TypeFullName(md, (TypeDefinitionHandle)h);
            case HandleKind.TypeReference:
                {
                    var tr = md.GetTypeReference((TypeReferenceHandle)h);
                    string ns = md.GetString(tr.Namespace);
                    string n = md.GetString(tr.Name);
                    return string.IsNullOrEmpty(ns) ? n : ns + "." + n;
                }
            case HandleKind.TypeSpecification:
                return "<typespec>";
            default:
                return "?";
        }
    }
}
