using System.Runtime.CompilerServices;

namespace AutoReconnect.Scripts;

/// <summary>
/// CLR 加载 Core DLL 时立即触发，早于任何其它代码。
/// 初始化 Diag 和 HostInfoTracker，确保即使 [ModInitializer] 未被调用也已就绪。
/// </summary>
internal static class CoreBootstrap
{
#pragma warning disable CA2255
    [ModuleInitializer]
    public static void Initialize()
#pragma warning restore CA2255
    {
        Diag.Init();
        Diag.Log("--------------------------------------------");
        Diag.Log("AutoReconnectMinCore v0.9.34-core ModuleInitializer 触发");
        Diag.Log("--------------------------------------------");

        HostInfoTracker.Reset();
    }
}
