namespace NytroxRAT.Agent;

/// <summary>
/// Mirrors AsyncRAT's Settings class.
/// Builder uses dnlib to open the assembly, find this class,
/// and rewrite the static field values directly in the IL metadata.
/// No byte tags, no placeholders — just plain static fields.
/// </summary>
public static class Settings
{
    public static string Ip     = "127.0.0.1";
    public static string Port   = "9000";
    public static string Secret = "changeme";

    /// <summary>When true, connects with wss:// and accepts self-signed TLS (dev / operator cert).</summary>
    public static bool UseWss = false;

    public static string GetUrl() => $"{(UseWss ? "wss" : "ws")}://{Ip}:{Port}";
}
