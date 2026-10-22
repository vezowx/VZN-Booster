using System.Diagnostics;

namespace VznBooster.Core.Startup;

public enum StartupSource { CurrentUserRun, MachineRun, MachineRun32, UserStartupFolder }

public sealed record StartupEntry(
    string Id, string Name, StartupSource Source, string Command, string? ExecutablePath, bool ExecutableExists,
    string? Publisher, bool Enabled, bool CanChange, bool LooksSecurityRelated, string? Note)
{
    public string SourceText => Source switch
    {
        StartupSource.CurrentUserRun => "Registro (usuário atual)",
        StartupSource.MachineRun => "Registro (todos os usuários)",
        StartupSource.MachineRun32 => "Registro (todos os usuários, 32 bits)",
        _ => "Pasta Inicializar do usuário"
    };
}

public static class StartupCommandParser
{
    /// <summary>Extrai o caminho do executável de uma linha de comando de inicialização (com ou sem aspas). Variáveis de ambiente são expandidas.</summary>
    public static string? ExtractExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var c = Environment.ExpandEnvironmentVariables(command.Trim());
        if (c.Length == 0) return null;
        if (c[0] == '"')
        {
            var end = c.IndexOf('"', 1);
            return end > 1 ? c[1..end] : null;
        }
        var exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return c[..(exe + 4)];
        var space = c.IndexOf(' ');
        return space > 0 ? c[..space] : c;
    }
}

public static class StartupApprovedState
{
    /// <summary>Convenção do Gerenciador de Tarefas: primeiro byte par (02, 06) = ativado; ímpar (03, 07) = desativado; ausente = ativado.</summary>
    public static bool IsEnabled(byte[]? value) => value is null || value.Length == 0 || (value[0] & 1) == 0;

    public static byte[] Enabled() => new byte[12] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

    public static byte[] Disabled(DateTimeOffset when)
    {
        var data = new byte[12];
        data[0] = 0x03;
        BitConverter.GetBytes(when.UtcDateTime.ToFileTimeUtc()).CopyTo(data, 4);
        return data;
    }
}

/// <summary>Lista as entradas de inicialização do usuário e do computador (somente leitura). Serviços e tarefas agendadas não são cobertos.</summary>
public sealed class StartupEnumerator
{
    public const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ApprovedRunPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string ApprovedFolderPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";
    public const string MachineRunPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    public const string MachineRun32Path = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    public const string MachineApprovedRunPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string MachineApprovedRun32Path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";

    // Heurística PROTETIVA (não classifica segurança nem malícia): itens que parecem de segurança exigem reconhecimento extra para desativar.
    private static readonly string[] SecurityKeywords =
    {
        "defender", "security", "antivirus", "antimalware", "firewall", "avast", "avg", "bitdefender", "kaspersky",
        "norton", "mcafee", "eset", "malwarebytes", "sophos", "trend micro", "windows security"
    };

    private readonly IStartupRegistry _registry;
    private readonly Func<string, IReadOnlyList<string>> _listFolderFiles;
    private readonly string? _userStartupFolder;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, string?> _publisherOf;

    public StartupEnumerator(IStartupRegistry registry, Func<string, IReadOnlyList<string>>? listFolderFiles = null,
        string? userStartupFolder = null, Func<string, bool>? fileExists = null, Func<string, string?>? publisherOf = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _listFolderFiles = listFolderFiles ?? (f => DefaultList(f));
        _userStartupFolder = userStartupFolder;
        _fileExists = fileExists ?? (f => File.Exists(f));
        _publisherOf = publisherOf ?? (f => DefaultPublisher(f));
    }

    public IReadOnlyList<StartupEntry> Enumerate()
    {
        var list = new List<StartupEntry>();
        AddRegistry(list, StartupHive.CurrentUser, RunPath, ApprovedRunPath, StartupSource.CurrentUserRun, canChange: true, null);
        AddRegistry(list, StartupHive.LocalMachine, MachineRunPath, MachineApprovedRunPath, StartupSource.MachineRun, false,
            "Exige administrador para alterar; não suportado nesta versão.");
        AddRegistry(list, StartupHive.LocalMachine, MachineRun32Path, MachineApprovedRun32Path, StartupSource.MachineRun32, false,
            "Exige administrador para alterar; não suportado nesta versão.");

        if (!string.IsNullOrWhiteSpace(_userStartupFolder))
            foreach (var file in _listFolderFiles(_userStartupFolder).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                var approved = _registry.GetBinary(StartupHive.CurrentUser, ApprovedFolderPath, fileName);
                list.Add(new StartupEntry($"{StartupSource.UserStartupFolder}|{fileName}", Path.GetFileNameWithoutExtension(fileName),
                    StartupSource.UserStartupFolder, file, null, true, null, StartupApprovedState.IsEnabled(approved), true,
                    LooksSecurity(fileName), "Atalho na pasta Inicializar; o destino do atalho não é lido nesta versão."));
            }
        return list;
    }

    private void AddRegistry(List<StartupEntry> list, StartupHive hive, string runPath, string approvedPath,
        StartupSource source, bool canChange, string? note)
    {
        foreach (var name in _registry.ValueNames(hive, runPath).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(name)) continue;
            var command = _registry.GetString(hive, runPath, name) ?? string.Empty;
            var exe = StartupCommandParser.ExtractExecutable(command);
            var exists = exe is not null && _fileExists(exe);
            var publisher = exists && exe is not null ? _publisherOf(exe) : null;
            var enabled = StartupApprovedState.IsEnabled(_registry.GetBinary(hive, approvedPath, name));
            var entryNote = !exists && exe is not null ? "O executável não foi encontrado: a entrada pode estar quebrada (programa removido)." : note;
            list.Add(new StartupEntry($"{source}|{name}", name, source, command, exe, exists, publisher, enabled, canChange,
                LooksSecurity(name, command, publisher), entryNote));
        }
    }

    private static bool LooksSecurity(params string?[] texts)
    {
        var all = string.Join(' ', texts.Where(t => t is not null)).ToLowerInvariant();
        return SecurityKeywords.Any(all.Contains);
    }

    private static IReadOnlyList<string> DefaultList(string folder)
    {
        try { return Directory.Exists(folder) ? Directory.EnumerateFiles(folder).ToList() : Array.Empty<string>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private static string? DefaultPublisher(string path)
    {
        try { return FileVersionInfo.GetVersionInfo(path).CompanyName is { Length: > 0 } company ? company : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
}
