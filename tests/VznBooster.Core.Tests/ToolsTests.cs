using System.ComponentModel;
using System.Diagnostics;
using VznBooster.Core.Commands;
using VznBooster.Core.Tools;
using Xunit;

namespace VznBooster.Core.Tests;

public class ToolsTests
{
    [Fact]
    public void Catalogo_de_atalhos_e_consistente_e_sem_argumentos()
    {
        var all = ToolCatalog.Shortcuts;
        Assert.Equal(all.Count, all.Select(t => t.Id).Distinct().Count());
        Assert.All(all, t =>
        {
            Assert.Contains(t.Group, ToolCatalog.GroupOrder);
            Assert.False(string.IsNullOrWhiteSpace(t.Description));
            Assert.DoesNotContain(' ', t.Target);                              // sem argumentos embutidos
            Assert.DoesNotContain('\\', t.Target);
            Assert.DoesNotContain('/', t.Target);
            Assert.True(t.Target.EndsWith(".exe") || t.Target.EndsWith(".msc") || t.Target.EndsWith(".cpl") || t.Target.StartsWith("ms-settings:"));
        });
    }

    [Fact]
    public void Abre_a_ferramenta_da_lista_pelo_shell_sem_elevar()
    {
        ProcessStartInfo? started = null;
        var r = new ToolLauncher(psi => started = psi).Launch("taskmgr");
        Assert.True(r.Success);
        Assert.Equal("taskmgr.exe", started!.FileName);
        Assert.True(started.UseShellExecute);
        Assert.NotEqual("runas", started.Verb);
        Assert.Empty(started.Arguments);
    }

    [Fact]
    public void Ferramenta_inexistente_na_edicao_do_windows_gera_mensagem_sem_quebrar()
    {
        var r = new ToolLauncher(_ => throw new Win32Exception(2)).Launch("mdsched");
        Assert.False(r.Success);
        Assert.Contains("edição do Windows", r.Message);
    }

    [Fact]
    public void Id_fora_da_lista_lanca_e_nada_e_iniciado()
    {
        var started = false;
        Assert.Throws<ArgumentException>(() => new ToolLauncher(_ => started = true).Launch("cmd /c del *"));
        Assert.False(started);
    }

    [Fact]
    public void Verificacoes_de_integridade_sao_somente_leitura_com_aviso_e_exigem_administrador()
    {
        foreach (var id in new[] { "tool.sfc-verify", "tool.dism-check" })
        {
            Assert.True(OperationCatalog.TryGet(id, out var op));
            Assert.Equal(RiskLevel.ReadOnly, op!.Risk);
            Assert.True(op.RequiresAdmin);
            Assert.False(op.RequiresRestart);
            Assert.False(string.IsNullOrWhiteSpace(op.Warning));
            Assert.DoesNotContain(op.Arguments, a => a.Contains("repair", StringComparison.OrdinalIgnoreCase)
                                                    || a.Contains("scannow", StringComparison.OrdinalIgnoreCase)
                                                    || a.Contains("restorehealth", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Resumo_da_saida_remove_nulos_e_linhas_vazias_e_mantem_as_ultimas()
    {
        var text = "l\0i\0n\0h\0a\0 1\0\r\n\r\n   \r\nlinha 2\nlinha 3\r\nlinha 4";
        Assert.Equal("linha 3\nlinha 4", OutputSummary.LastLines(text, 2));
        Assert.Equal("linha 1\nlinha 2\nlinha 3\nlinha 4", OutputSummary.LastLines(text, 10));
        Assert.Equal(string.Empty, OutputSummary.LastLines(null));
        Assert.Equal(string.Empty, OutputSummary.LastLines("  \r\n "));
    }
}
