namespace VznBooster.Core.Cleaning;

/// <summary>
/// Catálogo de categorias, organizado em grupos. Fora do escopo por decisão de projeto: logs de eventos e de segurança,
/// pontos de restauração, backups, arquivo de paginação, arquivos pessoais e dados de navegadores como senhas, cookies,
/// sessões, histórico, favoritos e extensões. Não incluído por exigir parar serviços: cache de download do Windows Update.
/// </summary>
public static class CleanCategoryCatalog
{
    private static readonly (string Key, string Name, string[] UserData)[] Chromium =
    {
        ("chrome", "Google Chrome", new[] { "Google", "Chrome", "User Data" }),
        ("edge", "Microsoft Edge", new[] { "Microsoft", "Edge", "User Data" }),
        ("brave", "Brave", new[] { "BraveSoftware", "Brave-Browser", "User Data" }),
        ("vivaldi", "Vivaldi", new[] { "Vivaldi", "User Data" }),
    };
    private static readonly string[] ChromiumCacheDirs = { "Cache", "Code Cache", "GPUCache" };

    public static IReadOnlyList<CleanCategory> Build(CleanEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(env);
        var local = env.LocalAppData;
        var list = new List<CleanCategory>
        {
            // A — Arquivos temporários
            new("temp.user", "Arquivos temporários do usuário", "Conteúdo da pasta TEMP do usuário.",
                Safe(env.UserTemp), "*", true, false, false, null, true, CleanGroups.Temporary, CleanRisk.Low),
            new("temp.windows", "Arquivos temporários do Windows", "Conteúdo de Windows\\Temp. A exclusão costuma exigir administrador; sem ele, itens protegidos são ignorados.",
                Safe(Combine(env.WindowsDir, "Temp")), "*", true, true, false, null, false, CleanGroups.Temporary, CleanRisk.Low),

            // B — Cache do Windows
            new("cache.thumbnails", "Cache de miniaturas", "Arquivos thumbcache do Explorer; o Windows os recria. O Explorer pode mantê-los em uso.",
                Safe(Combine(local, "Microsoft", "Windows", "Explorer")), "thumbcache_*.db", false, false, false, null, false, CleanGroups.WindowsCache, CleanRisk.Low),
            new("cache.shaders", "Cache de shaders do DirectX", "Reconstruível, mas jogos podem demorar mais na próxima abertura enquanto o cache é refeito.",
                Safe(Combine(local, "D3DSCache")), "*", true, false, false, null, false, CleanGroups.WindowsCache, CleanRisk.Low),
            new("cache.inetcache", "Cache de conteúdo da web do Windows", "Arquivos temporários de internet (INetCache) usados por componentes do Windows; são recriados quando necessário.",
                Safe(Combine(local, "Microsoft", "Windows", "INetCache")), "*", true, false, false, null, false, CleanGroups.WindowsCache, CleanRisk.Low),
        };

        // C — Navegadores (uma categoria por perfil real)
        foreach (var (key, name, userData) in Chromium)
        {
            var root = Combine(local, userData);
            if (!Directory.Exists(root)) continue;
            foreach (var profile in Directory.EnumerateDirectories(root).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var pn = Path.GetFileName(profile);
                if (pn != "Default" && !pn.StartsWith("Profile ", StringComparison.Ordinal)) continue;
                var roots = ChromiumCacheDirs.Select(sub => Path.Combine(profile, sub)).Where(Directory.Exists).ToList();
                AddBrowser(list, key, name, pn, roots);
            }
        }
        var firefox = Combine(local, "Mozilla", "Firefox", "Profiles");
        if (Directory.Exists(firefox))
            foreach (var profile in Directory.EnumerateDirectories(firefox).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var cache = Path.Combine(profile, "cache2");
                if (Directory.Exists(cache)) AddBrowser(list, "firefox", "Mozilla Firefox", Path.GetFileName(profile), new List<string> { cache });
            }

        // D — Sistema e armazenamento
        list.Add(new("sys.recyclebin", "Lixeira", "Esvazia a Lixeira de todas as unidades.",
            Array.Empty<string>(), "*", false, false, false, null, false, CleanGroups.System, CleanRisk.Moderate,
            "Os itens da Lixeira serão excluídos permanentemente e não poderão ser restaurados. A operação não pode ser interrompida depois de iniciada.",
            IsRecycleBin: true));
        list.Add(new("sys.deliveryopt", "Cache da Otimização de Entrega", "Arquivos de atualizações baixados para compartilhamento (Delivery Optimization); o Windows baixa de novo se precisar. Requer administrador.",
            Safe(Combine(env.WindowsDir, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache")),
            "*", true, true, false, null, false, CleanGroups.System, CleanRisk.Low));
        list.Add(new("diag.wer.user", "Relatórios de erro do Windows (usuário)", "Relatórios de erro já enviados ou na fila do usuário (WER).",
            SafeMany(new[] { "ReportArchive", "ReportQueue", "Temp" }.Select(s => Combine(local, "Microsoft", "Windows", "WER", s))),
            "*", true, false, false, null, false, CleanGroups.System, CleanRisk.Low));
        list.Add(new("diag.wer.system", "Relatórios de erro do Windows (sistema)", "Relatórios de erro do sistema em ProgramData. Requer administrador.",
            SafeMany(new[] { "ReportArchive", "ReportQueue" }.Select(s => Combine(env.CommonAppData, "Microsoft", "Windows", "WER", s))),
            "*", true, true, false, null, false, CleanGroups.System, CleanRisk.Low));
        list.Add(new("diag.crashdumps", "Despejos de falha de aplicativos", "Arquivos .dmp gerados quando aplicativos travam (pasta CrashDumps do usuário).",
            Safe(Combine(local, "CrashDumps")), "*.dmp", false, false, false, null, false, CleanGroups.System, CleanRisk.Moderate,
            "Esses arquivos são usados para investigar travamentos de aplicativos. Depois de removidos, não será possível analisar falhas passadas."));
        list.Add(new("diag.minidump", "Minidumps do Windows", "Arquivos .dmp de falhas graves (telas azuis) em Windows\\Minidump. Requer administrador.",
            Safe(Combine(env.WindowsDir, "Minidump")), "*.dmp", false, true, false, null, false, CleanGroups.System, CleanRisk.Moderate,
            "Esses arquivos são usados para diagnosticar telas azuis. Depois de removidos, falhas passadas não poderão ser analisadas."));

        // E — Limpeza avançada
        list.Add(new("adv.prefetch", "Prefetch do Windows", "Arquivos .pf em Windows\\Prefetch. Somente .pf; nenhuma outra pasta ou arquivo é tocado. Requer administrador.",
            Safe(Combine(env.WindowsDir, "Prefetch")), "*.pf", false, true, false, null, false, CleanGroups.Advanced, CleanRisk.Advanced,
            "O Prefetch faz parte dos mecanismos que o Windows usa para otimizar o carregamento de aplicativos. A limpeza rotineira normalmente NÃO é recomendada: " +
            "os primeiros carregamentos depois da limpeza tendem a ficar mais lentos até o Windows refazer esses dados. " +
            "Não há promessa de mais FPS nem de menos ping."));
        return list;
    }

    private static void AddBrowser(List<CleanCategory> list, string key, string browser, string profile, List<string> roots)
    {
        var safe = SafeMany(roots);
        if (safe.Count == 0) return;
        list.Add(new CleanCategory($"browser.{key}.{profile}", $"{browser} — perfil {profile}",
            "Somente cache (não remove senhas, cookies, sessões, histórico, favoritos nem extensões).",
            safe, "*", true, false, true, $"Feche o {browser} antes de limpar.", false, CleanGroups.Browsers, CleanRisk.Low));
    }

    /// <summary>Base vazia devolve vazio: nunca deixa um caminho relativo virar pasta do diretório atual.</summary>
    private static string Combine(string first, params string[] rest) =>
        string.IsNullOrWhiteSpace(first) ? string.Empty : Path.Combine(new[] { first }.Concat(rest).ToArray());

    /// <summary>Descarta raízes vazias ou que sejam raiz de unidade.</summary>
    private static IReadOnlyList<string> Safe(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Array.Empty<string>();
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(full);
        if (root is not null && Path.TrimEndingDirectorySeparator(root) == full) return Array.Empty<string>();
        return new[] { full };
    }

    private static IReadOnlyList<string> SafeMany(IEnumerable<string> paths) => paths.SelectMany(Safe).ToList();
}
