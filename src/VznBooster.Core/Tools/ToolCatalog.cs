using System.ComponentModel;
using System.Diagnostics;

namespace VznBooster.Core.Tools;

/// <summary>Atalho para uma ferramenta nativa do Windows. É só um atalho: o VZN Booster não reimplementa nem altera a ferramenta.</summary>
public sealed record ToolShortcut(string Id, string Name, string Description, string Target, string Group);

public static class ToolCatalog
{
    public const string Diagnostics = "Diagnóstico e monitoramento";
    public const string Storage = "Armazenamento";
    public const string Connectivity = "Rede e atualizações";
    public const string Power = "Energia e dispositivos";

    public static IReadOnlyList<string> GroupOrder { get; } = new[] { Diagnostics, Storage, Connectivity, Power };

    public static IReadOnlyList<ToolShortcut> Shortcuts { get; } = new[]
    {
        new ToolShortcut("taskmgr", "Gerenciador de Tarefas", "Processos, desempenho e inicialização.", "taskmgr.exe", Diagnostics),
        new ToolShortcut("msinfo32", "Informações do sistema", "Resumo detalhado de hardware e software.", "msinfo32.exe", Diagnostics),
        new ToolShortcut("resmon", "Monitor de Recursos", "CPU, memória, disco e rede por processo.", "resmon.exe", Diagnostics),
        new ToolShortcut("perfmon", "Monitor de Desempenho", "Contadores de desempenho do Windows.", "perfmon.exe", Diagnostics),
        new ToolShortcut("eventvwr", "Visualizador de Eventos", "Registros de eventos do Windows (somente consulta aqui).", "eventvwr.msc", Diagnostics),
        new ToolShortcut("dxdiag", "Diagnóstico do DirectX", "Informações de vídeo, som e DirectX.", "dxdiag.exe", Diagnostics),
        new ToolShortcut("diskmgmt", "Gerenciamento de Disco", "Volumes e partições. Cuidado: permite apagar partições.", "diskmgmt.msc", Storage),
        new ToolShortcut("storagesense", "Armazenamento (Configurações)", "Uso de espaço e Sensor de Armazenamento do Windows.", "ms-settings:storagesense", Storage),
        new ToolShortcut("cleanmgr", "Limpeza de Disco", "Ferramenta nativa de limpeza do Windows.", "cleanmgr.exe", Storage),
        new ToolShortcut("netstatus", "Status da rede (Configurações)", "Estado e propriedades das conexões.", "ms-settings:network-status", Connectivity),
        new ToolShortcut("winupdate", "Windows Update (Configurações)", "Atualizações do Windows. O VZN Booster nunca desativa atualizações.", "ms-settings:windowsupdate", Connectivity),
        new ToolShortcut("powercfg", "Opções de Energia", "Planos de energia do Windows.", "powercfg.cpl", Power),
        new ToolShortcut("devmgmt", "Gerenciador de Dispositivos", "Dispositivos e drivers instalados.", "devmgmt.msc", Power),
        new ToolShortcut("mdsched", "Diagnóstico de Memória do Windows", "Testa a RAM. Pede para reiniciar o computador para executar o teste.", "mdsched.exe", Power),
    };

    public static ToolShortcut? Find(string id) => Shortcuts.FirstOrDefault(t => t.Id == id);
}

public sealed record ToolLaunchResult(bool Success, string Message);

/// <summary>Abre uma ferramenta da lista fixa (nunca um texto arbitrário), sem argumentos e sem elevar privilégios.</summary>
public sealed class ToolLauncher
{
    private readonly Action<ProcessStartInfo> _start;

    public ToolLauncher(Action<ProcessStartInfo>? start = null) => _start = start ?? (psi => Process.Start(psi)?.Dispose());

    public ToolLaunchResult Launch(string toolId)
    {
        var tool = ToolCatalog.Find(toolId) ?? throw new ArgumentException($"Ferramenta desconhecida: {toolId}", nameof(toolId));
        try
        {
            _start(new ProcessStartInfo(tool.Target) { UseShellExecute = true });
            return new ToolLaunchResult(true, $"{tool.Name} aberto.");
        }
        catch (Win32Exception ex)
        {
            return new ToolLaunchResult(false, $"Não foi possível abrir {tool.Name} (código {ex.NativeErrorCode}). Ela pode não existir nesta edição do Windows.");
        }
        catch (InvalidOperationException)
        {
            return new ToolLaunchResult(false, $"Não foi possível abrir {tool.Name}.");
        }
    }
}

public static class OutputSummary
{
    /// <summary>Últimas linhas não vazias de uma saída de ferramenta (sem caracteres nulos), para exibir um resumo legível.</summary>
    public static string LastLines(string? text, int maxLines = 12)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var lines = text.Replace("\0", string.Empty)
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
        return string.Join("\n", lines.Skip(Math.Max(0, lines.Count - maxLines)));
    }
}
