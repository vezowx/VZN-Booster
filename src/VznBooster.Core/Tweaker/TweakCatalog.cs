namespace VznBooster.Core.Tweaker;

/// <summary>
/// Catálogo de ajustes. Somente valores de HKCU, um a um, sem serviços, sem segurança (Defender, firewall, atualizações) e sem
/// "pacotes" de debloat. Cada ajuste explica o que muda, o risco real, o benefício esperado (sem prometer FPS/ping) e a reversão.
/// Ainda não disponíveis: planos de energia, ajustes de rede/DNS pela tela, inicialização e serviços.
/// </summary>
public static class TweakCatalog
{
    public const string Gaming = "Gaming";
    public const string Interface = "Interface do Windows";
    public const string Privacy = "Privacidade e segundo plano";

    public static IReadOnlyList<string> CategoryOrder { get; } = new[] { Gaming, Interface, Privacy };
    public static IReadOnlyList<string> UnavailableCategories { get; } = new[]
    {
        "Sistema e desempenho (planos de energia)", "Network (DNS e conectividade pela tela de ajustes)", "Inicialização e serviços"
    };

    public static IReadOnlyList<TweakDefinition> All { get; } = new[]
    {
        new TweakDefinition("gaming.gamemode", "Modo de Jogo", Gaming,
            "Mantém o Modo de Jogo do Windows explicitamente ligado.",
            @"Define AutoGameModeEnabled e AllowAutoGameMode = 1 em HKCU\Software\Microsoft\GameBar. O Modo de Jogo prioriza o jogo em primeiro plano e reduz atividade de tarefas em segundo plano durante a partida.",
            "Garantir que o recurso nativo de priorização de jogos esteja ativo.",
            "Pode ajudar a manter o desempenho estável em PCs com poucos recursos. O ganho varia por jogo e hardware e não é garantido.",
            "PCs modestos com muitos programas em segundo plano durante partidas.",
            "Em casos raros o Modo de Jogo causa engasgos em alguns jogos; basta restaurar para voltar ao estado anterior.",
            TweakRisk.Low, TweakBenefit.Expected, RestartRequirement.None,
            new[]
            {
                new RegistryChange(@"Software\Microsoft\GameBar", "AutoGameModeEnabled", RegKind.DWord, "1"),
                new RegistryChange(@"Software\Microsoft\GameBar", "AllowAutoGameMode", RegKind.DWord, "1"),
            },
            CompatibilityNote: "Em instalações recentes o Modo de Jogo já vem ativado; se o valor não existir, o Windows usa o padrão. Este ajuste apenas o torna explícito."),

        new TweakDefinition("gaming.gamedvr", "Gravação em segundo plano (captura de jogos)", Gaming,
            "Desativa a gravação contínua de clipes da Barra de Jogos.",
            @"Define GameDVR_Enabled = 0 (HKCU\System\GameConfigStore) e AppCaptureEnabled = 0 (HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR).",
            "Evitar a captura em segundo plano enquanto você joga.",
            "Reduz a atividade de captura em segundo plano. O impacto em FPS costuma ser pequeno e depende do hardware; não é garantido.",
            "Quando você não usa a captura de clipes da Barra de Jogos e quer menos atividade durante jogos.",
            "A Barra de Jogos deixa de gravar clipes automaticamente (\"gravar os últimos minutos\"). Gravadores de outros programas continuam funcionando. Feche e abra os jogos para aplicar.",
            TweakRisk.Low, TweakBenefit.Expected, RestartRequirement.None,
            new[]
            {
                new RegistryChange(@"System\GameConfigStore", "GameDVR_Enabled", RegKind.DWord, "0"),
                new RegistryChange(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", RegKind.DWord, "0"),
            }),

        new TweakDefinition("ui.transparency", "Efeitos de transparência", Interface,
            "Desativa a transparência de menus, barra de tarefas e janelas.",
            @"Define EnableTransparency = 0 em HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize.",
            "Interface mais sólida e, em PCs modestos, um pequeno alívio de GPU.",
            "Principalmente visual. Pode reduzir levemente o uso de GPU em computadores modestos; não é mensurável de forma confiável em todos.",
            "PCs com GPU integrada fraca ou preferência por visual sem translucidez.",
            "Menu Iniciar, barra de tarefas e janelas perdem o efeito translúcido.",
            TweakRisk.Low, TweakBenefit.Visual, RestartRequirement.None,
            new[] { new RegistryChange(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", RegKind.DWord, "0") }),

        new TweakDefinition("ui.minanimate", "Animação ao minimizar e maximizar", Interface,
            "Desativa a animação de janelas ao minimizar e maximizar.",
            @"Define MinAnimate = ""0"" (texto) em HKCU\Control Panel\Desktop\WindowMetrics.",
            "Janelas respondem sem esperar a animação.",
            "Principalmente visual/comportamental; a sensação de rapidez varia de pessoa para pessoa.",
            "Quando você prefere uma interface mais direta.",
            "Janelas minimizam e maximizam sem animação, o que pode parecer abrupto. Pode ser necessário encerrar a sessão do Windows e entrar de novo para aplicar.",
            TweakRisk.Low, TweakBenefit.Visual, RestartRequirement.SignOut,
            new[] { new RegistryChange(@"Control Panel\Desktop\WindowMetrics", "MinAnimate", RegKind.String, "0") }),

        new TweakDefinition("ui.menudelay", "Atraso para abrir submenus", Interface,
            "Reduz o atraso de abertura de submenus de 400 ms (padrão) para 100 ms.",
            @"Define MenuShowDelay = ""100"" (texto) em HKCU\Control Panel\Desktop.",
            "Menus respondem mais rápido ao passar o mouse.",
            "Principalmente comportamental: submenus abrem mais cedo.",
            "Quando os menus parecem lentos para abrir.",
            "Submenus podem abrir sem querer ao passar o mouse rapidamente. Pode ser necessário encerrar a sessão e entrar de novo para aplicar.",
            TweakRisk.Low, TweakBenefit.Visual, RestartRequirement.SignOut,
            new[] { new RegistryChange(@"Control Panel\Desktop", "MenuShowDelay", RegKind.String, "100") }),

        new TweakDefinition("privacy.adid", "ID de publicidade", Privacy,
            "Impede que aplicativos usem o ID de publicidade da sua conta.",
            @"Define Enabled = 0 em HKCU\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo.",
            "Reduzir o rastreamento entre aplicativos para anúncios.",
            "Comportamental, sem efeito de desempenho. Anúncios continuam aparecendo, apenas menos direcionados.",
            "Quando você prefere menos personalização de anúncios.",
            "Aplicativos deixam de usar o ID para anúncios personalizados; nenhum recurso do Windows deixa de funcionar.",
            TweakRisk.Low, TweakBenefit.NotMeasurable, RestartRequirement.None,
            new[] { new RegistryChange(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", RegKind.DWord, "0") }),

        new TweakDefinition("privacy.tailored", "Experiências personalizadas com dados de diagnóstico", Privacy,
            "Impede o uso de dados de diagnóstico para dicas e anúncios personalizados.",
            @"Define TailoredExperiencesWithDiagnosticDataEnabled = 0 em HKCU\Software\Microsoft\Windows\CurrentVersion\Privacy.",
            "Reduzir a personalização baseada em diagnóstico.",
            "Comportamental, sem efeito de desempenho. Não desativa a telemetria em si.",
            "Quando você prefere sugestões menos personalizadas do Windows.",
            "Dicas, sugestões e anúncios do Windows ficam menos personalizados.",
            TweakRisk.Low, TweakBenefit.NotMeasurable, RestartRequirement.None,
            new[] { new RegistryChange(@"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", RegKind.DWord, "0") }),

        new TweakDefinition("privacy.bgapps", "Aplicativos da Microsoft Store em segundo plano", Privacy,
            "Impede que aplicativos da Store executem em segundo plano (Windows 10).",
            @"Define GlobalUserDisabled = 1 (HKCU\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications) e BackgroundAppGlobalToggle = 0 (HKCU\Software\Microsoft\Windows\CurrentVersion\Search), os valores usados pela página de Configurações do Windows 10. No Windows 11 a configuração é por aplicativo, por isso este ajuste fica indisponível.",
            "Menos atividade de aplicativos da Store quando fechados.",
            "Esperado, dependente do cenário: menos tarefas em segundo plano e, em notebooks, possível economia de bateria. Não verificado por medição neste PC.",
            "PCs com muitos aplicativos da Store instalados e poucos recursos.",
            "Aplicativos da Store deixam de atualizar blocos, sincronizar e receber notificações em segundo plano; e-mail, calendário e mensageiros da Store podem atrasar avisos.",
            TweakRisk.Moderate, TweakBenefit.Expected, RestartRequirement.None,
            new[]
            {
                new RegistryChange(@"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", RegKind.DWord, "1"),
                new RegistryChange(@"Software\Microsoft\Windows\CurrentVersion\Search", "BackgroundAppGlobalToggle", RegKind.DWord, "0"),
            },
            MaxBuild: 21999,
            CompatibilityNote: "Disponível apenas no Windows 10. No Windows 11 a permissão é configurada por aplicativo."),
    };

    public static IReadOnlyList<TweakProfile> Profiles { get; } = new[]
    {
        new TweakProfile("balanced", "Equilibrado",
            "Ajustes simples e de baixo risco: Modo de Jogo explícito e menos personalização de anúncios.",
            new[] { "gaming.gamemode", "privacy.adid", "privacy.tailored" }),
        new TweakProfile("gaming", "Gaming",
            "Reduz atividade em segundo plano durante jogos. Não toca em Defender, firewall nem atualizações e não promete mais FPS ou menos ping.",
            new[] { "gaming.gamemode", "gaming.gamedvr", "privacy.bgapps" }),
        new TweakProfile("lowbackground", "Baixa interferência em segundo plano",
            "Menos captura, personalização e atividade de aplicativos em segundo plano.",
            new[] { "gaming.gamedvr", "privacy.adid", "privacy.tailored", "privacy.bgapps" }),
        new TweakProfile("custom", "Personalizado", "Escolha manualmente os ajustes que deseja.", Array.Empty<string>()),
    };

    public static TweakDefinition? Find(string id) => All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));
}
