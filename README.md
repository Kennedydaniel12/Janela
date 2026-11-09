# Janela

Rastreador de tempo open source para Windows 11. A visão de hoje e da semana separa uso por aplicativo, categoria e projeto, com uma linha do tempo de atividade, reprodução e inatividade.

## Gerar o instalador

Na máquina de compilação, instale o SDK .NET 10 e o Inno Setup 6.3 ou posterior. Execute na pasta do projeto:

```powershell
.\Build-Installer.ps1
```

O resultado fica em `dist\Janela-Setup-x64.exe`. O instalador configura o Janela para o usuário atual e cria um atalho no menu Iniciar. Inclui o runtime .NET e usa o runtime Evergreen do WebView2 do Windows. O histórico em `%LOCALAPPDATA%\Janela` é preservado ao desinstalar.

Para detectar vídeos, carregue a extensão da subpasta `extension` que o instalador incluiu e siga os passos abaixo. Ao registrar a ponte, use o caminho do executável instalado, por padrão `%LOCALAPPDATA%\Programs\Janela\Janela.exe`.

## Compilar e executar sem instalador

Use o SDK .NET 10 e o runtime Evergreen do WebView2. Em PowerShell, nesta pasta:

```powershell
dotnet publish .\Janela.csproj -c Release -r win-x64 --self-contained true -o .\publish
.\publish\Janela.exe
```

Mantenha a pasta de publicação junta. Fechar o painel recolhe o aplicativo para a bandeja. Use **Sair** no menu da bandeja para encerrar a coleta.

## Atualizações automáticas

O Janela instalado consulta o GitHub ao iniciar e a cada seis horas. Quando há uma versão nova, baixa o instalador, confere tamanho e SHA-256 publicados pelo GitHub e aplica a atualização quando você não está interagindo com o painel. O aplicativo reabre na bandeja e preserva o histórico. A versão portátil não consulta atualizações.

Cada push na `main` dispara o fluxo de publicação. O GitHub Releases é a fonte das atualizações. O instalador ainda não tem assinatura Authenticode.

## Detectar vídeo no Chrome e no Edge

1. Abra `chrome://extensions` ou `edge://extensions`, ative o modo de desenvolvedor e carregue a pasta `extension` como extensão sem compactação.
2. Copie o ID mostrado pelo navegador. Registre a ponte para cada navegador onde instalou a extensão:

   ```powershell
   .\Register-Extension.ps1 -Browser Chrome -ExtensionId '<id-do-chrome>' -Executable .\publish\Janela.exe
   .\Register-Extension.ps1 -Browser Edge -ExtensionId '<id-do-edge>' -Executable .\publish\Janela.exe
   ```

3. Recarregue a extensão e as páginas de vídeo que já estavam abertas. Repita o registro após mover a pasta de publicação ou recompilar o executável.

O registro é feito só para o usuário atual. A extensão verifica se um elemento de vídeo HTML está avançando na aba ativa, inclusive em frames compatíveis. A ponte recebe somente o estado de reprodução. Players de desktop precisam anunciar uma sessão de vídeo em reprodução que corresponda ao app em foco. Vídeos que não expõem esses sinais seguem a regra normal de inatividade.

## Organizar seu tempo

Em **Aplicativos**, clique no nome de um app para criar uma regra. Preencha só a categoria para classificar o aplicativo inteiro. Crie outra regra com o trecho do título para atribuir o projeto. Em cada campo, a regra com trecho mais específico tem prioridade. Uma regra nova vale para os próximos intervalos. Para alterar um intervalo já salvo, clique nele na linha do tempo.

O SQLite fica em `%LOCALAPPDATA%\Janela\history.sqlite`. O histórico guarda nome do processo, início, fim, categoria, projeto e estado. O título da janela é consultado em memória. Trechos que você inserir em regras são salvos localmente. A coleta acontece na sessão em que o aplicativo está rodando.

## Verificação manual no Windows

- Confira painel, bandeja, teclado e temas claro e escuro a 100% e 150% de escala.
- Crie uma categoria geral para o VS Code e duas regras de projeto. Alterne os títulos e confirme os totais por categoria e projeto. Corrija um intervalo pelo painel.
- Ajuste a inatividade para 10 segundos para acelerar o teste. Em Chrome e Edge, deixe um vídeo mudo rodar sem interação e confira Reprodução. Pause depois do limite, troque de aba e repita com vídeo em segundo plano.
- Bloqueie e suspenda o Windows durante uma reprodução. Confira que o intervalo parado não foi contabilizado. Repita com pausa manual ativa antes de bloquear.
- Feche o painel e use outro aplicativo. Abra o painel pela bandeja para confirmar que a coleta continuou. Confira os totais na virada do dia.

Licença MIT.
