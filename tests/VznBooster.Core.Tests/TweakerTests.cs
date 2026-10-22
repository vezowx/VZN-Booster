using VznBooster.Core.Backup;
using VznBooster.Core.Logging;
using VznBooster.Core.Tweaker;
using Xunit;

namespace VznBooster.Core.Tests;

public class TweakCatalogTests
{
    [Fact]
    public void Catalogo_e_consistente()
    {
        var all = TweakCatalog.All;
        Assert.Equal(all.Count, all.Select(t => t.Id).Distinct().Count());
        foreach (var t in all)
        {
            Assert.Contains(t.Category, TweakCatalog.CategoryOrder);
            Assert.False(string.IsNullOrWhiteSpace(t.Name));
            Assert.False(string.IsNullOrWhiteSpace(t.Summary));
            Assert.False(string.IsNullOrWhiteSpace(t.Technical));
            Assert.False(string.IsNullOrWhiteSpace(t.Goal));
            Assert.False(string.IsNullOrWhiteSpace(t.ExpectedBenefit));
            Assert.False(string.IsNullOrWhiteSpace(t.UsefulWhen));
            Assert.False(string.IsNullOrWhiteSpace(t.SideEffects));
            Assert.NotEmpty(t.Changes);
            Assert.False(t.NeedsAdmin);
            Assert.All(t.Changes, c =>
            {
                Assert.False(c.KeyPath.StartsWith("HKEY", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain("..", c.KeyPath);
                if (c.Kind == RegKind.DWord) Assert.True(long.TryParse(c.Desired, out _));
            });
        }
    }

    [Fact]
    public void Nenhum_ajuste_toca_em_seguranca_servicos_ou_atualizacoes()
    {
        var paths = TweakCatalog.All.SelectMany(t => t.Changes).Select(c => c.KeyPath).ToList();
        Assert.DoesNotContain(paths, p => p.Contains("Defender", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(paths, p => p.Contains("Firewall", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(paths, p => p.Contains("WindowsUpdate", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(paths, p => p.Contains(@"Services\", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Perfis_so_referenciam_ajustes_existentes_e_gaming_nao_mexe_em_seguranca()
    {
        foreach (var p in TweakCatalog.Profiles)
            Assert.All(p.TweakIds, id => Assert.NotNull(TweakCatalog.Find(id)));
        Assert.Contains(TweakCatalog.Profiles, p => p.Id == "custom" && p.TweakIds.Count == 0);
        var gaming = TweakCatalog.Profiles.Single(p => p.Id == "gaming");
        Assert.DoesNotContain(gaming.TweakIds, id => id.Contains("security") || id.Contains("update") || id.Contains("defender"));
    }

    [Fact]
    public void Snapshot_codifica_e_decodifica()
    {
        Assert.Equal("absent", ValueSnapshot.Absent.Encode());
        var s = new ValueSnapshot(true, RegKind.String, "a|b");
        var back = ValueSnapshot.Decode(s.Encode());
        Assert.Equal(RegKind.String, back.Kind);
        Assert.Equal("a|b", back.Value);
        Assert.Throws<FormatException>(() => ValueSnapshot.Decode("lixo"));
        Assert.Throws<FormatException>(() => ValueSnapshot.Decode("Nope|1"));
    }
}

public class TweakEngineTests
{
    private sealed class FakeRegistry : IRegistryAccessor
    {
        public Dictionary<(string, string), (RegKind Kind, string Value)> Values { get; } = new();
        public int Writes;
        public int FailOnWriteNumber = -1;     // 1-based; -1 = nunca
        public bool IgnoreWrites;
        public bool UnsupportedRead;
        public bool FailAllWritesAfterBackup;

        public ValueSnapshot Read(string keyPath, string valueName)
        {
            if (UnsupportedRead) throw new NotSupportedException("tipo binário");
            return Values.TryGetValue((keyPath, valueName), out var v) ? new ValueSnapshot(true, v.Kind, v.Value) : ValueSnapshot.Absent;
        }

        public void Write(string keyPath, string valueName, RegKind kind, string value)
        {
            Writes++;
            if (FailAllWritesAfterBackup || Writes == FailOnWriteNumber) throw new IOException("falha simulada");
            if (!IgnoreWrites) Values[(keyPath, valueName)] = (kind, value);
        }

        public void Delete(string keyPath, string valueName)
        {
            if (FailAllWritesAfterBackup) throw new IOException("falha simulada");
            Values.Remove((keyPath, valueName));
        }
    }

    private sealed class ListLog : IOperationLog
    {
        public List<OperationLogEntry> Entries { get; } = new();
        public Task WriteAsync(OperationLogEntry e, CancellationToken ct = default) { Entries.Add(e); return Task.CompletedTask; }
        public Task<IReadOnlyList<OperationLogEntry>> ReadAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<OperationLogEntry>>(Entries);
    }

    private sealed class Rig : IDisposable
    {
        private readonly TempDir _dir = new();
        public FakeRegistry Reg { get; } = new();
        public ListLog Log { get; } = new();
        public BackupStore Backups { get; }
        public TweakEngine Engine { get; }

        public Rig(int build = 19045)
        {
            Backups = new BackupStore(_dir.Dir);
            Engine = new TweakEngine(Reg, Backups, Log, () => build);
        }

        public void Dispose() => _dir.Dispose();
    }

    private const string Dvr = "gaming.gamedvr";            // 2 valores DWord, risco baixo
    private const string Anim = "ui.minanimate";           // 1 valor String, exige encerrar sessão
    private static TweakDefinition Def(string id) => TweakCatalog.Find(id)!;

    private static void SetDvrOriginal(FakeRegistry r, string gameDvr = "1", string appCapture = "1")
    {
        r.Values[(@"System\GameConfigStore", "GameDVR_Enabled")] = (RegKind.DWord, gameDvr);
        r.Values[(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled")] = (RegKind.DWord, appCapture);
    }

    // ---------- estado ----------

    [Fact]
    public void Estado_reflete_o_registro()
    {
        using var r = new Rig();
        Assert.Equal(TweakStatus.NotApplied, r.Engine.GetState(Def(Dvr)).Status);        // valores ausentes

        SetDvrOriginal(r.Reg, "0", "1");
        var partial = r.Engine.GetState(Def(Dvr));
        Assert.Equal(TweakStatus.Partial, partial.Status);
        Assert.Contains("GameDVR_Enabled: 0", partial.CurrentValues);

        SetDvrOriginal(r.Reg, "0", "0");
        Assert.Equal(TweakStatus.Applied, r.Engine.GetState(Def(Dvr)).Status);
    }

    [Fact]
    public async Task Valor_de_tipo_nao_suportado_deixa_o_estado_desconhecido_e_bloqueia_a_aplicacao()
    {
        using var r = new Rig();
        r.Reg.UnsupportedRead = true;
        Assert.Equal(TweakStatus.Unknown, r.Engine.GetState(Def(Dvr)).Status);
        var o = await r.Engine.ApplyAsync(Dvr, true);
        Assert.Equal(TweakResultKind.Failed, o.Kind);
        Assert.Equal(0, r.Reg.Writes);
    }

    [Fact]
    public async Task Ajuste_restrito_ao_windows_10_e_recusado_no_windows_11()
    {
        using var r = new Rig(build: 22631);
        var state = r.Engine.GetState(Def("privacy.bgapps"));
        Assert.False(state.Applicable);
        var o = await r.Engine.ApplyAsync("privacy.bgapps", true);
        Assert.Equal(TweakResultKind.Refused, o.Kind);
        Assert.Equal(0, r.Reg.Writes);
    }

    // ---------- aplicar ----------

    [Fact]
    public async Task Sem_confirmacao_nada_muda_e_nenhum_backup_e_criado()
    {
        using var r = new Rig();
        var o = await r.Engine.ApplyAsync(Dvr, false);
        Assert.Equal(TweakResultKind.Refused, o.Kind);
        Assert.Equal(0, r.Reg.Writes);
        Assert.Empty(await r.Backups.ListAsync());
    }

    [Fact]
    public async Task Ajuste_desconhecido_lanca()
    {
        using var r = new Rig();
        await Assert.ThrowsAsync<ArgumentException>(() => r.Engine.ApplyAsync("formatar.tudo", true));
    }

    [Fact]
    public async Task Aplica_so_aquele_ajuste_salva_backup_verifica_e_registra()
    {
        using var r = new Rig();
        SetDvrOriginal(r.Reg);
        r.Reg.Values[(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency")] = (RegKind.DWord, "1");   // outro ajuste: não deve mudar

        var o = await r.Engine.ApplyAsync(Dvr, true);

        Assert.Equal(TweakResultKind.Success, o.Kind);
        Assert.True(o.Verified);
        Assert.Equal("0", r.Reg.Values[(@"System\GameConfigStore", "GameDVR_Enabled")].Value);
        Assert.Equal("1", r.Reg.Values[(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency")].Value);

        var b = (await r.Backups.ListAsync()).Single();
        Assert.Equal(o.BackupId, b.Id);
        Assert.Equal("Aplicado e verificado", b.Status);
        Assert.Equal("DWord|1", b.Before["reg.0.before"]);
        Assert.Equal("DWord|0", b.After!["reg.0.after"]);

        var entry = r.Log.Entries.Single();
        Assert.Equal("Tweaker", entry.Category);
        Assert.Equal("tweak.apply:" + Dvr, entry.Operation);
        Assert.Equal(OperationResult.Success, entry.Result);
        Assert.Equal(b.Id, entry.BackupId);
        Assert.Contains("antes:", entry.StdOut);
    }

    [Fact]
    public async Task Ja_aplicado_nao_cria_backup_nem_escreve()
    {
        using var r = new Rig();
        SetDvrOriginal(r.Reg, "0", "0");
        var o = await r.Engine.ApplyAsync(Dvr, true);
        Assert.Equal(TweakResultKind.AlreadyApplied, o.Kind);
        Assert.Equal(0, r.Reg.Writes);
        Assert.Empty(await r.Backups.ListAsync());
    }

    [Fact]
    public async Task Ajuste_que_exige_encerrar_sessao_avisa_na_mensagem()
    {
        using var r = new Rig();
        var o = await r.Engine.ApplyAsync(Anim, true);
        Assert.Equal(TweakResultKind.Success, o.Kind);
        Assert.Contains("Encerre a sessão", o.Message);
    }

    [Fact]
    public async Task Escrita_sem_efeito_nao_e_declarada_como_verificada()
    {
        using var r = new Rig();
        SetDvrOriginal(r.Reg);
        r.Reg.IgnoreWrites = true;
        var o = await r.Engine.ApplyAsync(Dvr, true);
        Assert.Equal(TweakResultKind.Failed, o.Kind);
        Assert.False(o.Verified);
        Assert.Equal("Aplicado, verificação falhou", (await r.Backups.ListAsync()).Single().Status);
        Assert.Equal(OperationResult.Failed, r.Log.Entries.Single().Result);
    }

    [Fact]
    public async Task Falha_no_meio_da_aplicacao_desfaz_o_que_foi_escrito()
    {
        using var r = new Rig();
        SetDvrOriginal(r.Reg);
        r.Reg.FailOnWriteNumber = 2;                 // a 2ª escrita falha; a 1ª já tinha sido feita

        var o = await r.Engine.ApplyAsync(Dvr, true);

        Assert.Equal(TweakResultKind.Failed, o.Kind);
        Assert.Contains("restaurado", o.Message);
        Assert.Equal("1", r.Reg.Values[(@"System\GameConfigStore", "GameDVR_Enabled")].Value);
        Assert.Equal("1", r.Reg.Values[(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled")].Value);
        Assert.Equal("Falhou; estado anterior restaurado", (await r.Backups.ListAsync()).Single().Status);
        Assert.Equal(OperationResult.Failed, r.Log.Entries.Single().Result);
    }

    // ---------- restaurar ----------

    [Fact]
    public async Task Restaura_valores_anteriores_preservando_o_tipo()
    {
        using var r = new Rig();
        r.Reg.Values[(@"Control Panel\Desktop\WindowMetrics", "MinAnimate")] = (RegKind.String, "1");
        var applied = await r.Engine.ApplyAsync(Anim, true);
        Assert.Equal("0", r.Reg.Values[(@"Control Panel\Desktop\WindowMetrics", "MinAnimate")].Value);

        var o = await r.Engine.RestoreAsync(applied.BackupId!, true);

        Assert.Equal(TweakResultKind.Success, o.Kind);
        Assert.True(o.Verified);
        Assert.Equal((RegKind.String, "1"), r.Reg.Values[(@"Control Panel\Desktop\WindowMetrics", "MinAnimate")]);
        var b = (await r.Backups.ListAsync()).Single();
        Assert.Equal("Revertido e verificado", b.Status);
        Assert.Equal("tweak.restore:" + Anim, r.Log.Entries.Last().Operation);
    }

    [Fact]
    public async Task Restaura_valor_que_nao_existia_apagando_o_valor()
    {
        using var r = new Rig();                     // nada existia antes
        var applied = await r.Engine.ApplyAsync(Dvr, true);
        Assert.Equal(2, r.Reg.Values.Count);

        var o = await r.Engine.RestoreAsync(applied.BackupId!, true);

        Assert.Equal(TweakResultKind.Success, o.Kind);
        Assert.Empty(r.Reg.Values);
    }

    [Fact]
    public async Task Conflito_e_detectado_e_so_sobrescreve_com_confirmacao_adicional()
    {
        using var r = new Rig();
        SetDvrOriginal(r.Reg);
        var applied = await r.Engine.ApplyAsync(Dvr, true);
        r.Reg.Values[(@"System\GameConfigStore", "GameDVR_Enabled")] = (RegKind.DWord, "1");   // outro programa mudou depois

        var conflict = await r.Engine.RestoreAsync(applied.BackupId!, true);
        Assert.Equal(TweakResultKind.Conflict, conflict.Kind);
        Assert.Contains(conflict.Conflicts, c => c.Contains("GameDVR_Enabled"));
        Assert.Equal("0", r.Reg.Values[(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled")].Value);   // nada foi revertido
        Assert.Equal("Aplicado e verificado", (await r.Backups.ListAsync()).Single().Status);

        var forced = await r.Engine.RestoreAsync(applied.BackupId!, true, overrideConflict: true);
        Assert.Equal(TweakResultKind.Success, forced.Kind);
        Assert.Equal("1", r.Reg.Values[(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled")].Value);
    }

    [Fact]
    public async Task Falha_durante_a_reversao_e_reportada_como_parcial()
    {
        using var r = new Rig();
        SetDvrOriginal(r.Reg);
        var applied = await r.Engine.ApplyAsync(Dvr, true);
        r.Reg.FailAllWritesAfterBackup = true;

        var o = await r.Engine.RestoreAsync(applied.BackupId!, true);

        Assert.Equal(TweakResultKind.Failed, o.Kind);
        Assert.Equal("Reversão falhou; estado parcial — verifique", (await r.Backups.ListAsync()).Single().Status);
        Assert.Equal(OperationResult.Failed, r.Log.Entries.Last().Result);
    }

    [Fact]
    public async Task Backup_ja_revertido_nao_pode_ser_restaurado_de_novo()
    {
        using var r = new Rig();
        var applied = await r.Engine.ApplyAsync(Dvr, true);
        await r.Engine.RestoreAsync(applied.BackupId!, true);
        var again = await r.Engine.RestoreAsync(applied.BackupId!, true);
        Assert.Equal(TweakResultKind.Refused, again.Kind);
    }

    [Fact]
    public async Task Restauracao_exige_confirmacao_e_id_valido()
    {
        using var r = new Rig();
        var applied = await r.Engine.ApplyAsync(Dvr, true);
        Assert.Equal(TweakResultKind.Refused, (await r.Engine.RestoreAsync(applied.BackupId!, false)).Kind);
        Assert.Equal(TweakResultKind.Refused, (await r.Engine.RestoreAsync("../x", true)).Kind);
        Assert.Equal(TweakResultKind.Refused, (await r.Engine.RestoreAsync(new string('a', 32), true)).Kind);
    }

    [Fact]
    public async Task Backup_adulterado_que_aponta_para_outra_chave_e_recusado()
    {
        using var r = new Rig();
        var data = new Dictionary<string, string>
        {
            ["tweakId"] = Dvr,
            ["reg.0.path"] = @"SYSTEM\CurrentControlSet\Services\WinDefend",     // não é a chave do catálogo
            ["reg.0.name"] = "Start",
            ["reg.0.before"] = "DWord|2",
            ["reg.1.path"] = @"Software\Microsoft\Windows\CurrentVersion\GameDVR",
            ["reg.1.name"] = "AppCaptureEnabled",
            ["reg.1.before"] = "DWord|1"
        };
        var fake = await r.Backups.CreateAsync(TweakEngine.BackupOperation, data, "x");
        await r.Backups.CompleteAsync(fake.Id, new Dictionary<string, string>(), "Aplicado e verificado");

        var o = await r.Engine.RestoreAsync(fake.Id, true);

        Assert.Equal(TweakResultKind.Refused, o.Kind);
        Assert.Equal(0, r.Reg.Writes);
        Assert.Empty(r.Reg.Values);
    }

    [Fact]
    public async Task Localiza_o_backup_ativo_mais_recente()
    {
        using var r = new Rig();
        Assert.Null(await r.Engine.FindActiveBackupAsync(Dvr));

        var first = await r.Engine.ApplyAsync(Dvr, true);
        Assert.Equal(first.BackupId, (await r.Engine.FindActiveBackupAsync(Dvr))!.Id);

        await r.Engine.RestoreAsync(first.BackupId!, true);
        Assert.Null(await r.Engine.FindActiveBackupAsync(Dvr));          // revertido: não está mais "ativo"

        var second = await r.Engine.ApplyAsync(Dvr, true);
        Assert.Equal(second.BackupId, (await r.Engine.FindActiveBackupAsync(Dvr))!.Id);
        Assert.Null(await r.Engine.FindActiveBackupAsync(Anim));
    }
}
