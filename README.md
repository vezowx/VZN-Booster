# VZN Booster

Otimizador/diagnóstico para Windows 10/11 x64 (C# / .NET 8 / WPF / MVVM).

## Estado atual (honesto)

Última verificação no Windows (autor): `dotnet build` ok nos 3 projetos e `dotnet test` com **250 de 250** testes passando (inclui todas as rodadas até Logs e encerrar programas).
Esta rodada acrescentou código **ainda não compilado/testado** (marcado abaixo).

| Funcionalidade | Estado |
| --- | --- |
| Núcleo: logs com redação, executor de comandos com catálogo fechado, backups, configurações, privacidade (máscaras) | Concluído e verificado |
| Network: adaptadores (IP/gateway mascarados + olho), teste de latência ICMP | Concluído e verificado (rede real só manualmente) |
| DNS: perfis, plano, aplicar com backup/verificação, restaurar (núcleo) | Núcleo verificado com runner simulado |
| **DNS na interface (Network): ver plano, aplicar, restaurar; restaurar também em Restauração** | **Novo, não verificado** (exige administrador; endereços dos perfis não conferidos em fonte oficial: o diálogo exige reconhecimento) |
| **Comparador de DNS (consultas UDP reais, IPv4, só aponta vencedor se a diferença for significativa)** | **Novo, não verificado** (testes de formato do pacote e do critério escritos) |
| **Barra lateral recolhida: rótulos ocultos (antes ficava um pedaço da letra visível)** | **Correção, não verificada** |
| Home: CPU/RAM em tempo real, discos, conexão, espaço recuperável, recomendações, últimas ações | Concluído e verificado (valores reais só manualmente) |
| Cleaner: 5 grupos, perfis de navegador, Lixeira, Prefetch avançado, exclusão com confirmação | Concluído e verificado (exclusão real só manualmente) |
| Tweaker: 8 ajustes HKCU, perfis, backup/rollback/conflito; Restauração e Logs | Concluído e verificado com registro simulado; efeito real só manualmente |
| Configurações: privacidade, animações, restaurar padrões | Concluído e verificado |
| **Permissões: reiniciar como administrador (UAC)** | **Novo, não verificado** |
| **Ponto de restauração do Windows (cria e verifica por contagem)** | **Novo, não verificado** (testes com runner simulado escritos) |
| **Desempenho: CPU total e por núcleo, frequência reportada, RAM/memória comprometida, processos (busca, ordenação, tipo observável), consumo do próprio app** | **Novo, não verificado** (sem GPU/disco/temperatura; sem encerrar processos) |
| **Hardware: inventário com fonte por valor (informado/medido/indisponível) e relatório TXT sem dados pessoais por padrão** | **Novo, não verificado** (módulos de RAM, núcleos físicos, tipo/saúde de disco, taxa do monitor e sensores indisponíveis) |
| **Inicialização: lista (registro do usuário e do computador, pasta Inicializar), editor, estado, executável ausente; ativar/desativar entradas do USUÁRIO pelo mecanismo do Gerenciador de Tarefas, com backup, verificação, log, conflito e restauração** | **Novo, não verificado** (itens do computador são só leitura; itens que parecem de segurança exigem reconhecimento extra; serviços e tarefas agendadas não cobertos) |
| **Ferramentas: 14 atalhos para ferramentas nativas (identificados como atalhos) e verificações SFC /verifyonly e DISM /CheckHealth (somente leitura, administrador, cancelável)** | **Novo, não verificado** (nenhum reparo é executado) |
| Tweaker: planos de energia, rede/DNS, inicialização e serviços | Não iniciado |
| **Gaming Network (Valorant, Rocket League, Fortnite): ESTIMATIVA INDIRETA via handshake TCP até endpoints regionais públicos da AWS; nunca "Medido"; servidor da partida NÃO é medido** | **Novo, não verificado** (testes com sonda simulada escritos) |
| **Diagnóstico de rede (gateway ICMP, DNS real, proxy, indícios de VPN) com achados priorizados e limiares fixos** | **Novo, não verificado** (testes escritos) |
| **Correções de conectividade (cache DNS, DHCP, Winsock, TCP/IP) com confirmação, medição antes/depois e log** | **Novo, não verificado**; exigem administrador; Winsock/TCP/IP exigem reinício e não têm comparação |
| **Detecção de VPN/proxy por indícios (nunca afirma que a VPN está ativa)** | **Novo, não verificado** |
| Exportação de relatório de hardware | Novo, não verificado |
| **Logs: filtros (texto, resultado, categoria, período), exportação TXT/JSON com dados pessoais ocultados por padrão e aviso da ocultação** | **Novo, não verificado** (testes escritos) |
| **Encerrar programa (Desempenho): fechamento normal → forçado só em 2º passo confirmado; sistema, serviços, segurança, shell e o próprio app são protegidos; tudo registrado** | **Novo, não verificado** (testes com controlador simulado) |
| Exportação de diagnósticos de rede, gráficos além de CPU/RAM, GPU/temperaturas | Não iniciado (GPU e temperaturas aparecem como indisponíveis) |
| Instalador / publicação | Não iniciado |

**Limitações conhecidas**
- Os valores de registro do Tweaker foram escolhidos de memória a partir do que as Configurações do Windows gravam: valide cada ajuste manualmente (veja `docs/TESTES-MANUAIS.md`).
- Gaming Network: nenhum dos três jogos tem destino de teste oficial verificado; os destinos usados são nomes de endpoints regionais da AWS (`ec2.<região>.amazonaws.com`, padrão documentado pela AWS, não conferido por mim em tempo real) e o mapeamento jogo→região é aproximado. Serve só como referência de proximidade.
- Os endereços de DNS em `DnsProfiles` não foram conferidos nas fontes oficiais (`VerifiedAgainstOfficialSource = false`); Control D está omitido.
- A elevação reinicia o aplicativo inteiro como administrador (escolha explícita do usuário). Um auxiliar elevado só para a operação ainda não existe.
- Sem sombras nos cards e sem barra de rolagem customizada (custo de renderização em PCs modestos).
- Glifos de ícone (Segoe MDL2 Assets) escolhidos de memória; algum pode aparecer como quadrado.
- Cache de download do Windows Update fica fora do Cleaner (exigiria parar serviços); caches de aplicativos (Discord, Teams) ainda não estão incluídos.

**Testes manuais no Windows:** `docs/TESTES-MANUAIS.md`.

## Executar o aplicativo (Windows, .NET 8 SDK)
    dotnet run --project src/VznBooster.Desktop

## Compilar e testar (.NET 8 SDK)
    dotnet test VznBooster.sln

## Pendências de verificação
- Endereços DNS em `DnsProfiles` devem ser conferidos nas URLs oficiais (`VerifiedAgainstOfficialSource` = false).
- Control D omitido até a conferência.
