using VznBooster.Core.Backup;
using VznBooster.Core.Logging;
using VznBooster.Core.Startup;
using VznBooster.Core.Tweaker;
using Xunit;

namespace VznBooster.Core.Tests;

public class StartupParsingTests
{
    [Theory]
    [InlineData("\"C:\\Apps\\Discord\\Update.exe\" --processStart Discord.exe", "C:\\Apps\\Discord\\Update.exe")]
    [InlineData("C:\\Program Files\\App\\app.exe /minimized", "C:\\Program Files\\App\\app.exe")]
    [InlineData("C:\\Tools\\script.cmd arg", "C:\\Tools\\script.cmd")]
    [InlineData("  \"C:\\X\\y.exe\"  ", "C:\\X\\y.exe")]
    public void Extrai_o_executavel(string command, string expected) => Assert.Equal(expected, StartupCommandParser.ExtractExecutable(command));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"sem fechar")]
    public void Entradas_invalidas_nao_geram_caminho(string command) => Assert.Null(StartupCommandParser.ExtractExecutable(command));

    [Fact]
    public void Estado_aprovado_segue_a_convencao_do_gerenciador_de_tarefas()
    {
        Assert.True(StartupApprovedState.IsEnabled(null));
        Assert.True(StartupApprovedState.IsEnabled(Array.Empty<byte>()));
        Assert.True(StartupApprovedState.IsEnabled(new byte[] { 0x02, 0, 0 }));
        Assert.True(StartupApprovedState.IsEnabled(new byte[] { 0x06, 0, 0 }));
        Assert.False(StartupApprovedState.IsEnabled(new byte[] { 0x03, 0, 0 }));
        Assert.False(StartupApprovedState.IsEnabled(new byte[] { 0x07, 0, 0 }));

        var disabled = StartupApprovedState.Disabled(DateTimeOffset.UtcNow);
        Assert.Equal(12, disabled.Length);
        Assert.Equal(0x03, disabled[0]);
        Assert.False(StartupApprovedState.IsEnabled(disabled));
        Assert.True(StartupApprovedState.IsEnabled(StartupApprovedState.Enabled()));
    }
}

public class StartupEngineTests
{
    private sealed class FakeStartupRegistry : IStartupRegistry
    {
        public Dictionary<(StartupHive, string, string), string> Strings { get; } = new();
        public Dictionary<(string, string), byte[]> Binaries { get; } = new();    // somente HKCU (escrita)
        public Dictionary<(StartupHive, string, string), byte[]> MachineBinaries { get; } = new();
        public bool FailWrites;

        public IReadOnlyList<string> ValueNames(StartupHive hive, string path) =>
            Strings.Keys.Where(k => k.Item1 == hive && k.Item2 == path).Select(k => k.Item3).ToList();
        public string? GetString(StartupHive hive, string path, string name) => Strings.TryGetValue((hive, path, name), out var v) ? v : null;
        public byte[]? GetBinary(StartupHive hive, string path, string name) =>
            hive == StartupHive.CurrentUser ? (Binaries.TryGetValue((path, name), out var b) ? b : null)
                                            : (MachineBinaries.TryGetValue((hive, path, name), out var m) ? m : null);
        public void SetBinary(string path, string name, byte[] data)
        {
            if (FailWrites) throw new IOException("falha simulada");
            Binaries[(path, name)] = data;
        }
        public void DeleteValue(string path, string name)
        {
            if (FailWrites) throw new IOException("falha simulada");
            Binaries.Remove((path, name));
        }
    }

    private sealed class ListLog : IOperationLog
    {
        public List<OperationLogEntry> Entries { get; } = new();
        public Task WriteAsync(OperationLogEntry e, CancellationToken ct = default) { Entries.Add(e); return Task.CompletedTask; }
        public Task<IReadOnlyList<OperationLogEntry>> ReadAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<OperationLogEntry>>(Entries);
    }

    private const string Folder = @"C:\Users\x\Startup";

    private sealed class Rig : IDisposable
    {
        private readonly TempDir _dir = new();
        public FakeStartupRegistry Reg { get; } = new();
        public ListLog Log { get; } = new();
        public BackupStore Backups { get; }
        public StartupEngine Engine { get; }
        public List<string> FolderFiles { get; } = new();

        public Rig()
        {
            Reg.Strings[(StartupHive.CurrentUser, StartupEnumerator.RunPath, "Discord")] = "\"C:\\Apps\\Discord\\Update.exe\" --processStart Discord.exe";
            Reg.Strings[(StartupHive.CurrentUser, StartupEnumerator.RunPath, "Antigo")] = "C:\\Old\\gone.exe /x";
            Reg.Strings[(StartupHive.CurrentUser, StartupEnumerator.RunPath, "Windows Defender Notifier")] = "\"C:\\Apps\\wdn.exe\"";
            Reg.Strings[(StartupHive.LocalMachine, StartupEnumerator.MachineRunPath, "SecurityHealth")] = "C:\\Windows\\System32\\SecurityHealthSystray.exe";
            FolderFiles.Add(Folder + @"\App.lnk");
            FolderFiles.Add(Folder + @"\desktop.ini");

            Backups = new BackupStore(_dir.Dir);
            var enumerator = new StartupEnumerator(Reg, _ => FolderFiles, Folder,
                fileExists: p => !p.Contains("gone"), publisherOf: p => p.Contains("Discord") ? "Discord Inc." : null);
            Engine = new StartupEngine(Reg, enumerator, Backups, Log);
        }

        public StartupEntry Entry(string id) => Engine.Enumerate().Single(e => e.Id == id);
        public void Dispose() => _dir.Dispose();
    }

    private const string DiscordId = "CurrentUserRun|Discord";

    [Fact]
    public void Enumera_entradas_com_origem_editor_estado_e_possibilidade_de_alterar()
    {
        using var r = new Rig();
        r.Reg.Binaries[(StartupEnumerator.ApprovedRunPath, "Discord")] = StartupApprovedState.Disabled(DateTimeOffset.UtcNow);

        var all = r.Engine.Enumerate();

        var discord = all.Single(e => e.Id == DiscordId);
        Assert.False(discord.Enabled);
        Assert.True(discord.CanChange);
        Assert.Equal("Discord Inc.", discord.Publisher);
        Assert.Equal(@"C:\Apps\Discord\Update.exe", discord.ExecutablePath);

        var antigo = all.Single(e => e.Name == "Antigo");
        Assert.False(antigo.ExecutableExists);
        Assert.Contains("não foi encontrado", antigo.Note);

        var machine = all.Single(e => e.Source == StartupSource.MachineRun);
        Assert.False(machine.CanChange);
        Assert.True(machine.LooksSecurityRelated);

        var folder = all.Single(e => e.Source == StartupSource.UserStartupFolder);
        Assert.Equal("App", folder.Name);
        Assert.DoesNotContain(all, e => e.Name.Contains("desktop"));
    }

    [Fact]
    public async Task Desativa_salva_backup_verifica_registra_e_nao_apaga_a_entrada()
    {
        using var r = new Rig();
        var o = await r.Engine.SetEnabledAsync(DiscordId, enable: false, userConfirmed: true);

        Assert.Equal(TweakResultKind.Success, o.Kind);
        Assert.True(o.Verified);
        Assert.False(r.Entry(DiscordId).Enabled);
        Assert.True(r.Reg.Strings.ContainsKey((StartupHive.CurrentUser, StartupEnumerator.RunPath, "Discord")));   // a entrada continua existindo

        var b = (await r.Backups.ListAsync()).Single();
        Assert.Equal("Aplicado e verificado", b.Status);
        Assert.Equal("absent", b.Before["before"]);
        Assert.Equal("startup.disable:Discord", r.Log.Entries.Single().Operation);
        Assert.Equal(OperationResult.Success, r.Log.Entries.Single().Result);
    }

    [Fact]
    public async Task Reverte_voltando_ao_estado_anterior_inclusive_valor_inexistente()
    {
        using var r = new Rig();
        var applied = await r.Engine.SetEnabledAsync(DiscordId, false, true);

        var o = await r.Engine.RestoreAsync(applied.BackupId!, true);

        Assert.Equal(TweakResultKind.Success, o.Kind);
        Assert.True(r.Entry(DiscordId).Enabled);
        Assert.False(r.Reg.Binaries.ContainsKey((StartupEnumerator.ApprovedRunPath, "Discord")));
        Assert.Equal("Revertido e verificado", (await r.Backups.ListAsync()).Single().Status);
    }

    [Fact]
    public async Task Entrada_da_pasta_inicializar_usa_o_nome_do_arquivo()
    {
        using var r = new Rig();
        var o = await r.Engine.SetEnabledAsync("UserStartupFolder|App.lnk", false, true);
        Assert.Equal(TweakResultKind.Success, o.Kind);
        Assert.True(r.Reg.Binaries.ContainsKey((StartupEnumerator.ApprovedFolderPath, "App.lnk")));
    }

    [Fact]
    public async Task Recusas_sem_confirmacao_entrada_inexistente_e_entrada_do_computador()
    {
        using var r = new Rig();
        Assert.Equal(TweakResultKind.Refused, (await r.Engine.SetEnabledAsync(DiscordId, false, false)).Kind);
        Assert.Equal(TweakResultKind.Refused, (await r.Engine.SetEnabledAsync("CurrentUserRun|NaoExiste", false, true)).Kind);
        var machine = await r.Engine.SetEnabledAsync("MachineRun|SecurityHealth", false, true);
        Assert.Equal(TweakResultKind.Refused, machine.Kind);
        Assert.Contains("administrador", machine.Message);
        Assert.Empty(r.Reg.Binaries);
        Assert.Empty(await r.Backups.ListAsync());
    }

    [Fact]
    public async Task Item_que_parece_de_seguranca_exige_reconhecimento_adicional()
    {
        using var r = new Rig();
        var id = "CurrentUserRun|Windows Defender Notifier";
        var sem = await r.Engine.SetEnabledAsync(id, false, true);
        Assert.Equal(TweakResultKind.Refused, sem.Kind);
        Assert.Contains("segurança", sem.Message);
        Assert.Empty(r.Reg.Binaries);

        var com = await r.Engine.SetEnabledAsync(id, false, true, acknowledgeSecurity: true);
        Assert.Equal(TweakResultKind.Success, com.Kind);
    }

    [Fact]
    public async Task Ja_no_estado_pedido_nao_cria_backup()
    {
        using var r = new Rig();
        var o = await r.Engine.SetEnabledAsync(DiscordId, enable: true, userConfirmed: true);   // já ativada
        Assert.Equal(TweakResultKind.AlreadyApplied, o.Kind);
        Assert.Empty(await r.Backups.ListAsync());
    }

    [Fact]
    public async Task Falha_ao_escrever_restaura_e_registra_falha()
    {
        using var r = new Rig();
        r.Reg.FailWrites = true;
        var o = await r.Engine.SetEnabledAsync(DiscordId, false, true);
        Assert.Equal(TweakResultKind.Failed, o.Kind);
        Assert.StartsWith("Falhou", (await r.Backups.ListAsync()).Single().Status);
        Assert.Equal(OperationResult.Failed, r.Log.Entries.Single().Result);
        Assert.True(r.Entry(DiscordId).Enabled);
    }

    [Fact]
    public async Task Conflito_e_detectado_e_so_sobrescreve_com_confirmacao_adicional()
    {
        using var r = new Rig();
        var applied = await r.Engine.SetEnabledAsync(DiscordId, false, true);
        r.Reg.Binaries[(StartupEnumerator.ApprovedRunPath, "Discord")] = StartupApprovedState.Enabled();       // alguém reativou depois

        var conflict = await r.Engine.RestoreAsync(applied.BackupId!, true);
        Assert.Equal(TweakResultKind.Conflict, conflict.Kind);
        Assert.Equal("Aplicado e verificado", (await r.Backups.ListAsync()).Single().Status);

        var forced = await r.Engine.RestoreAsync(applied.BackupId!, true, overrideConflict: true);
        Assert.Equal(TweakResultKind.Success, forced.Kind);
    }

    [Fact]
    public async Task Restauracao_recusa_entrada_removida_backup_adulterado_e_ja_revertido()
    {
        using var r = new Rig();
        var applied = await r.Engine.SetEnabledAsync(DiscordId, false, true);

        var adulterado = await r.Backups.CreateAsync(StartupEngine.BackupOperation, new Dictionary<string, string>
        {
            ["entryId"] = DiscordId, ["name"] = "Discord", ["path"] = @"SYSTEM\CurrentControlSet\Services\WinDefend",
            ["valueName"] = "Discord", ["before"] = "absent"
        }, "x");
        await r.Backups.CompleteAsync(adulterado.Id, new Dictionary<string, string>(), "Aplicado e verificado");
        Assert.Equal(TweakResultKind.Refused, (await r.Engine.RestoreAsync(adulterado.Id, true)).Kind);

        await r.Engine.RestoreAsync(applied.BackupId!, true);
        Assert.Equal(TweakResultKind.Refused, (await r.Engine.RestoreAsync(applied.BackupId!, true)).Kind);        // já revertido

        var second = await r.Engine.SetEnabledAsync(DiscordId, false, true);
        r.Reg.Strings.Remove((StartupHive.CurrentUser, StartupEnumerator.RunPath, "Discord"));                       // programa desinstalado
        Assert.Equal(TweakResultKind.Refused, (await r.Engine.RestoreAsync(second.BackupId!, true)).Kind);
    }
}
