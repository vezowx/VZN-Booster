using System.Diagnostics.CodeAnalysis;

namespace VznBooster.Core.Commands;

public enum RiskLevel { ReadOnly, LowImpact, ReversibleChange, HighImpact }

/// <summary>Operação de sistema permitida. Argumentos são FIXOS: nenhuma entrada do usuário vira argumento.</summary>
public sealed record AllowedOperation(
    string Id, string Category, string Description, string FileName,
    IReadOnlyList<string> Arguments, RiskLevel Risk, bool RequiresAdmin, bool RequiresRestart,
    string Warning, TimeSpan Timeout);

public static class OperationCatalog
{
    private const string PowerShell = @"WindowsPowerShell\v1.0\powershell.exe";

    public static IReadOnlyList<AllowedOperation> All { get; } = new[]
    {
        new AllowedOperation("net.info", "Rede", "Exibe a configuração de rede (somente leitura).",
            "ipconfig.exe", new[] { "/all" }, RiskLevel.ReadOnly, false, false,
            "Somente leitura.", TimeSpan.FromSeconds(30)),
        new AllowedOperation("net.dns-flush", "Rede", "Limpa o cache do resolvedor DNS.",
            "ipconfig.exe", new[] { "/flushdns" }, RiskLevel.LowImpact, true, false,
            "Limpa apenas o cache DNS local; as próximas consultas podem ser um pouco mais lentas. Requer administrador.",
            TimeSpan.FromSeconds(30)),
        new AllowedOperation("net.dhcp-renew", "Rede", "Renova a concessão DHCP dos adaptadores configurados por DHCP.",
            "ipconfig.exe", new[] { "/renew" }, RiskLevel.ReversibleChange, true, false,
            "A conexão pode cair por alguns segundos e o endereço IP local pode mudar. Requer administrador.", TimeSpan.FromSeconds(90)),
        new AllowedOperation("tool.sfc-verify", "Ferramentas",
            "Verifica a integridade dos arquivos protegidos do Windows (SFC /verifyonly). Não repara nada.",
            "sfc.exe", new[] { "/verifyonly" }, RiskLevel.ReadOnly, true, false,
            "Pode levar vários minutos e usar bastante disco e CPU. Requer administrador. Apenas verifica; nenhum reparo é feito.",
            TimeSpan.FromMinutes(45)),
        new AllowedOperation("tool.dism-check", "Ferramentas",
            "Verifica se a imagem do Windows está marcada como corrompida (DISM /CheckHealth). Não repara nada.",
            "Dism.exe", new[] { "/Online", "/Cleanup-Image", "/CheckHealth" }, RiskLevel.ReadOnly, true, false,
            "Requer administrador. Apenas consulta o estado já registrado; nenhum reparo é feito.",
            TimeSpan.FromMinutes(10)),
        new AllowedOperation("restorepoint.count", "Restauração",
            "Conta os pontos de restauração criados pelo VZN Booster (somente leitura).",
            PowerShell, new[] { "-NoProfile", "-NonInteractive", "-Command",
                "(Get-ComputerRestorePoint -ErrorAction SilentlyContinue | Where-Object { $_.Description -like 'VZN Booster*' } | Measure-Object).Count" },
            RiskLevel.ReadOnly, true, false, "Somente leitura. Requer administrador.", TimeSpan.FromSeconds(60)),
        new AllowedOperation("restorepoint.create", "Restauração",
            "Cria um ponto de restauração do Windows chamado \"VZN Booster\".",
            PowerShell, new[] { "-NoProfile", "-NonInteractive", "-Command",
                "Checkpoint-Computer -Description 'VZN Booster' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop" },
            RiskLevel.LowImpact, true, false,
            "Requer administrador e a Proteção do Sistema ativada. O Windows costuma limitar a criação a um ponto a cada 24 horas. Um ponto de restauração não cobre todos os arquivos, aplicativos ou configurações.",
            TimeSpan.FromSeconds(180)),
        new AllowedOperation("net.winsock-reset", "Rede", "Redefine o catálogo Winsock.",
            "netsh.exe", new[] { "winsock", "reset" }, RiskLevel.HighImpact, true, true,
            "Pode causar perda temporária de conectividade e remove LSPs de VPN/antivírus. Requer administrador e reinicialização.",
            TimeSpan.FromSeconds(60)),
        new AllowedOperation("net.ip-reset", "Rede", "Redefine parâmetros TCP/IP.",
            "netsh.exe", new[] { "int", "ip", "reset" }, RiskLevel.HighImpact, true, true,
            "Redefine configurações TCP/IP e pode desconectar a rede. Requer administrador e reinicialização.",
            TimeSpan.FromSeconds(60)),
    };

    public static bool TryGet(string id, [NotNullWhen(true)] out AllowedOperation? op)
    {
        op = All.FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.Ordinal));
        return op is not null;
    }
}
