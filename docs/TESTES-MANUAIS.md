# Testes manuais no Windows (VZN Booster)

Os testes automatizados (`dotnet test`) usam registro, rede, processos e Lixeira simulados. Estes passos validam o que só existe no Windows real.
Faça nesta ordem, anotando o resultado. Se algo falhar, copie a mensagem exibida e a entrada correspondente em **Logs**.

Pré-requisitos: Windows 10 ou 11 x64, .NET 8 SDK, ponto de restauração do Windows criado manualmente antes dos testes mais arriscados (Prefetch, Lixeira, Tweaker).

## 0. Inicialização e navegação
1. `dotnet run --project src/VznBooster.Desktop`. A janela abre sem erro e sem administrador.
2. Clique em cada uma das 11 seções. Esperado: Home, Network, Cleaner, Tweaker, Restauração, Logs e Configurações com conteúdo real; as demais mostram "Em desenvolvimento".
3. Recolha e expanda a barra lateral (botão ☰). Em Configurações, marque "Reduzir animações" e repita: sem animação.

## 1. Privacidade
1. Network → adaptadores: IPs aparecem como `192.168.***.***` / `***.***.***.***` e IPv6 todo mascarado.
2. Clique no olho de "Endereços IP": só os IPs são revelados (gateway continua oculto). Passe o mouse no olho: a dica NÃO mostra o valor.
3. Aguarde o intervalo configurado (padrão 30 s) ou troque de seção: volta a ocultar.
4. "Usar gateway": a caixa de destino mostra "gateway padrão", nunca o endereço. Rode o teste: o resultado não exibe o endereço.
5. Home: nome do computador e do usuário ocultos; olho individual para cada um.
6. Configurações → "Manter visível durante a sessão": revele um dado, troque de seção, ele permanece. Volte ao padrão.
7. Abra o arquivo `%LOCALAPPDATA%\VznBooster\logs\operations.jsonl` depois de qualquer operação: nenhum IP completo, MAC ou segredo.

## 2. Network
1. Ping para o gateway (10 amostras): estatísticas coerentes com `ping` no CMD (a precisão é em ms inteiros).
2. Destino inexistente: mensagem de falha de resolução, sem travar. Cancele um teste longo: nenhum resultado parcial.
3. Desconecte o cabo/Wi-Fi e repita: perda de 100% e "INDISPONÍVEL", sem números inventados.

## 2b. DNS (Network)
1. "Comparar DNS": aparecem os provedores (e "DNS atual do adaptador", se houver) com média, mín, máx, jitter e falhas; o veredito só aponta um vencedor com diferença de pelo menos 3 ms e 20%. Rode duas vezes: os números variam, e o aplicativo deve dizer "sem diferença significativa" quando for o caso.
2. Em rede que bloqueia UDP/53, os servidores aparecem com falhas e o veredito indica dados insuficientes (sem inventar resultado).
3. "Ver plano" com adaptador e perfil escolhidos: mostra DNS atual (IPs mascarados), novo DNS e avisos; nada é alterado.
4. SEM administrador, "Aplicar DNS" recusa e orienta a reiniciar como administrador (nenhum backup é criado).
5. COM administrador: "Aplicar DNS" exige marcar o reconhecimento de endereços. Após aplicar, confira com `ipconfig /all` e em Restauração: backup "Aplicado e verificado" com o DNS anterior.
6. "Restaurar DNS anterior": volta ao automático (DHCP) ou aos servidores manuais anteriores; confira com `ipconfig /all`. Confirme que só o adaptador escolhido mudou.
7. Confira os endereços de cada perfil na documentação oficial do provedor antes de aplicar (o aplicativo avisa que ainda não os conferiu).

## 0b. Barra lateral
1. Recolha a barra lateral: só os ícones ficam visíveis, centralizados, sem nenhum pedaço de texto. Passe o mouse num ícone: a dica mostra o nome. Expanda de novo: os rótulos voltam.

## 2d. Diagnóstico e correções de rede
1. "Diagnosticar minha rede": lista os achados (do mais grave ao menos grave) ou diz que nada foi encontrado, com o aviso de que uma medição curta não é prova definitiva. Nada é alterado.
2. Compare gateway e DNS com `ping <gateway>` e `nslookup example.com <seu DNS>` no CMD.
3. VPN: conecte uma VPN e repita: deve aparecer "VPN detectada e aparentemente conectada" com o nome do adaptador e o aviso de que não confirma que o tráfego passa por ela. Desconecte: "Possível VPN, sem confirmação" ou "Nenhuma evidência".
4. Proxy: ative um proxy nas Configurações do Windows e repita: "Proxy configurado". O VZN Booster não altera proxies.
5. SEM administrador, qualquer correção recusa e orienta a reiniciar como administrador.
6. COM administrador: "Limpar cache DNS" abre o diálogo de confirmação e, depois, mostra os números antes → depois do gateway e do DNS com o aviso de que sucesso do comando não prova melhora. Confira `ipconfig /displaydns` antes/depois.
7. "Renovar concessão DHCP": a conexão pode cair por alguns segundos; o aplicativo espera e mede de novo.
8. "Redefinir Winsock" e "Redefinir TCP/IP" exigem marcar o reconhecimento e informam que é preciso REINICIAR; não há comparação antes/depois. Faça só se necessário.
9. Conferir em Logs: cada correção aparece com o resultado real.

## 2c. Gaming Network
1. Pesquise "valo" na caixa de busca: só o Valorant aparece. Escolha um jogo: as regiões aparecem.
2. "Estimar" para a região do Brasil: o resultado aparece como **ESTIMADO (indireto) — confiança Baixa**, com média/jitter/perda do handshake TCP e as observações dizendo que o servidor da partida NÃO é medido. Nunca deve aparecer como "MEDIDO".
3. Compare com `Test-NetConnection ec2.sa-east-1.amazonaws.com -Port 443` no PowerShell: o tempo deve ser da mesma ordem de grandeza.
4. Desconecte a rede e repita: "INDISPONÍVEL", sem números.
5. Confirme que nenhum IP de servidor de jogo aparece em lugar nenhum.

## 2e. Desempenho
1. Abra Desempenho: CPU total, uma barra por núcleo e a frequência reportada (com o aviso de que pode refletir o estado de energia). Compare com o Gerenciador de Tarefas (valores próximos, não idênticos).
2. RAM: usado/disponível/total e memória comprometida; o aviso de que o cache do Windows não é desperdício.
3. Processos: busca por nome e por PID; ordene por CPU e por RAM; confira que o tipo (Sistema/Serviço/Aplicativo/Segundo plano) faz sentido. Processos protegidos mostram CPU "—". Os caminhos ocultam seu nome de usuário.
4. Saia da tela: confira no Gerenciador de Tarefas que o VZN Booster para de consumir CPU extra. O consumo do próprio app aparece no card à direita.
5. GPU, atividade de disco e temperaturas aparecem como "ainda não coletado".

## 2f. Hardware
1. Compare modelo da CPU, BIOS, placas de vídeo, memória, discos, versão do Windows e resolução com "Informações do sistema" (msinfo32).
2. Cada linha mostra a fonte; o que não foi lido aparece como "indisponível" (módulos de RAM, núcleos físicos, SSD/HDD e SMART, taxa do monitor, temperaturas).
3. "Exportar relatório…" sem marcar a opção: abra o .txt e confirme que NÃO há nome do computador nem da conta. Marcando a opção, o diálogo avisa e o arquivo passa a incluí-los.

## 2g. Inicialização
1. Compare a lista com a aba "Inicialização" do Gerenciador de Tarefas: mesmos itens do usuário e mesmo estado (Ativada/Desativada). Itens do computador aparecem como somente leitura.
2. Desative um item de teste do usuário: o diálogo explica o efeito; depois, confira no Gerenciador de Tarefas que ele aparece como Desativado. O programa continua instalado e a entrada continua na lista.
3. Em Restauração, o backup "Inicialização — <nome>" aparece como "Aplicado e verificado"; restaure e confirme que o item volta ao estado anterior no Gerenciador de Tarefas.
4. Conflito: desative pelo VZN Booster, reative pelo Gerenciador de Tarefas e tente restaurar: o aplicativo avisa do conflito.
5. Itens com nome de antivírus/segurança exigem marcar o reconhecimento antes de desativar. Entradas cujo executável não existe mais aparecem com aviso.
6. O efeito vale a partir do próximo logon (faça logoff/logon para confirmar).

## 2h. Ferramentas
1. Cada "Abrir (atalho)" abre a ferramenta do Windows correspondente; em edições sem a ferramenta, aparece uma mensagem na barra de status (sem erro).
2. SEM administrador, as verificações recusam e orientam. COM administrador, "SFC" e "DISM" exigem confirmação e mostram o aviso de tempo e consumo; cancele uma no meio para ver que nada fica pendente.
3. Ao terminar, o resultado mostra o código de saída e as últimas linhas da saída; nenhum reparo é feito. Confira o registro em Logs.

## 2i. Logs (filtros e exportação) e encerrar programas
1. Logs: filtre por resultado, categoria e período; a busca de texto combina com os filtros. A linha de informação mostra quantos registros atendem.
2. "Exportar texto…" e "Exportar JSON…" sem marcar a opção: abra o arquivo e confirme que não há seu nome de usuário, nome do computador, IP, MAC nem segredos, e que o cabeçalho informa a ocultação. Marcando a opção, aparece o diálogo de confirmação e o cabeçalho passa a dizer que dados pessoais foram incluídos.
3. Desempenho → Processos: abra o Bloco de Notas, localize-o e use "Encerrar…". O diálogo explica o efeito; com texto não salvo, o Bloco de Notas pergunta se deseja salvar e o VZN Booster informa que o processo continua em execução e NADA é forçado.
4. Só então, se quiser, o segundo diálogo oferece "Forçar encerramento" com a caixa de reconhecimento. Confira em Logs `process.close:` / `process.kill:`.
5. Processos do sistema, serviços, componentes de segurança, o explorer e o próprio VZN Booster não têm o botão "Encerrar…".

## 3. Home
1. CPU e RAM se movem em tempo real; compare com o Gerenciador de Tarefas (valores próximos, não idênticos).
2. Saia da Home e confira no Gerenciador de Tarefas que o uso de CPU do VZN Booster cai em repouso.
3. Discos: espaço livre confere com o Explorador de Arquivos.
4. "Medir latência": só mede ao clicar.

## 4. Cleaner (comece pelo TEMP do usuário)
1. Analisar apenas "Arquivos temporários do usuário". Compare a estimativa com as Propriedades da pasta `%TEMP%`.
2. Limpar: o diálogo lista a categoria, avisa que não é reversível. Cancele uma vez (nada apagado), depois confirme.
3. Resultado: removidos, ignorados (arquivos em uso) e variação medida do espaço livre; confira em Logs.
4. Arquivo em uso: abra um arquivo na pasta TEMP com outro programa, limpe: o arquivo é ignorado e contado.
5. Navegador: com o Chrome ABERTO, limpar o cache dele deve ser pulado ("Pulada: aplicativo aberto"). Feche e limpe: logins e histórico permanecem.
6. **Lixeira**: coloque um arquivo de teste nela, analise (contagem confere) e esvazie. Confirme que esvaziou todas as unidades.
7. **Prefetch (avançado)**: sem administrador, a limpeza mostra itens ignorados. Com administrador, o diálogo exige marcar a caixa de reconhecimento; só arquivos `.pf` são removidos (`Layout.ini` fica).
8. Sem administrador, o aviso "Executando sem administrador" aparece no Cleaner.

## 5. Tweaker (um ajuste de baixo risco por vez, ex.: "Efeitos de transparência")
1. Abra o Tweaker: estados lidos do registro (Aplicado/Não aplicado). Escolher perfil ou marcar caixa NÃO aplica nada.
2. Aplicar "Efeitos de transparência": o diálogo mostra funcionamento, benefício, efeitos e reversão. Confirme: a transparência do Windows desliga; estado vira "Aplicado".
3. Logs: entrada `tweak.apply:ui.transparency` com parâmetros, "antes/depois" e backup relacionado. Restauração: backup "Aplicado e verificado".
4. Restaurar: estado anterior volta; backup vira "Revertido e verificado".
5. **Conflito**: aplique de novo, mude a transparência manualmente em Configurações do Windows, tente Restaurar: o aplicativo avisa do conflito e só sobrescreve após a segunda confirmação.
6. Ajustes que dependem de sessão (animação de minimizar, atraso de submenus): confirme que a mensagem pede para encerrar a sessão e que o efeito só aparece depois.
7. Windows 11: "Aplicativos da Store em segundo plano" aparece como indisponível.
8. Confirme no Editor do Registro que cada valor tocado é exatamente o descrito em "Detalhes e riscos", e que nada fora dessas chaves mudou.

## 6. Permissões e ponto de restauração
1. Configurações → Permissões: mostra "sem administrador". "Reiniciar como administrador" abre o diálogo e o UAC; cancelar o UAC mantém o app aberto com mensagem.
2. Restauração → "Criar ponto de restauração" sem administrador: recusa com orientação.
3. Com administrador e Proteção do Sistema ativada: cria e verifica ("criado e verificado"). Repita em seguida: o Windows pode limitar a um ponto por 24 horas; o aplicativo deve informar que nenhum novo ponto apareceu (e NÃO declarar sucesso).
4. Confira em "Criar um ponto de restauração" do Windows (rstrui.exe) que o ponto "VZN Booster" existe.

## 7. Falhas e limites (resiliência)
1. Feche o app durante uma limpeza grande: ao reabrir, nada fica corrompido; Logs mostra o que foi registrado.
2. Apague `%LOCALAPPDATA%\VznBooster\settings.json`: o app abre com os padrões (privacidade ligada).
3. Corrompa o `settings.json` (apague metade): o app abre com os padrões e avisa na barra de status.
