# LivePix Listener

Programa de bandeja do Windows para ouvir o som e a voz dos alertas do [LivePix](https://livepix.gg) no computador onde está o seu fone.

Ele existe para quem joga em um PC e transmite em outro. O widget toca no PC da live, então a mensagem do doador não chega no headset. O Listener fica aberto no PC do fone, sem navegador, e toca o mesmo áudio que o widget tocaria.

Não é um programa oficial do LivePix.

## Baixar

Baixe o [LivePixListener-Setup.exe](https://github.com/MajorrLuke/livepix-listener/releases/latest/download/LivePixListener-Setup.exe). Ele instala só para o seu usuário, sem senha de administrador. A página da versão fica em [Releases](https://github.com/MajorrLuke/livepix-listener/releases).

SHA256: `08841e50843fdbcf42891e3172308ccf9e6c5e165e8d34ea0bd8adde52063b82`

Na instalação, **Iniciar com o Windows** já vem marcado. Pode desmarcar. Depois dá para mudar de novo na janela do programa, no ícone da bandeja.

O Windows pode mostrar o SmartScreen, porque o instalador não é assinado. Em **Mais informações**, escolha **Executar mesmo assim**.

Para remover: Configurações do Windows, Aplicativos, LivePix Listener. O link do widget continua salvo.

## Usar

1. Instale e, se quiser, deixe marcado **Iniciar com o Windows**.
2. No LivePix, copie o link do widget de **notificação**. É o mesmo link que está no navegador do OBS, no formato `https://widget.livepix.gg/embed/...`.
3. Cole o link, ajuste o volume e clique em **Salvar**.
4. Feche a janela. O ícone verde fica na bandeja, perto do relógio. Se não aparecer, olhe na seta `^`.
5. Deixe o widget no OBS do PC da live, como já estava.

No widget, deixe ligados **Tocar som de alerta** e **Ler as mensagens usando Text to Speech** se quiser ouvir a voz. O programa toca os arquivos que o LivePix gera. Ele não cria outra voz.

O volume da janela multiplica o volume que você já configurou no LivePix. `100%` aqui é o volume original do widget.

**Testar som** baixa e toca o som de alerta desse widget, sem esperar uma doação.

A janela guarda os alertas que tocaram: hora, nome, valor e mensagem. A lista fica em `%AppData%\LivePixListener\alerts.json` e continua lá depois de fechar o programa. **Limpar** apaga esse histórico. Remover o programa não apaga essa lista nem o link do widget.

**Iniciar com o Windows** cria um atalho na pasta Inicializar do seu usuário. Se a instalação já marcou isso, a opção aparece ligada.

Abrir o programa uma segunda vez só traz a janela de volta. Para sair de verdade, use **Sair** no menu do ícone.

A configuração fica em `%AppData%\LivePixListener\config.json`.

## Não abra o widget no navegador

O site do widget entra na fila de alertas. Se você abrir o mesmo link no Chrome do PC do jogo, ele pode consumir a doação e ela não aparece na live.

Este programa só escuta o canal público do widget e toca o áudio. Ele não reserva e não confirma o alerta, então o OBS continua dono da fila.

## O que ele gasta

Um processo pequeno (o exe tem cerca de 55 KB), um ícone na bandeja e uma conexão websocket. O áudio só é baixado quando chega um alerta. Não abre Chrome, Electron nem OBS.

## Privacidade

O link do widget já é público: é o mesmo que vai no OBS. O programa fala apenas com `webservice.livepix.gg` e `pubsub.livepix.gg`. Não pede login, não tem anúncio e não envia o texto das mensagens para outro servidor.

## Compilar

No Windows 10 ou 11, com o .NET Framework que já vem no sistema:

```bat
build.bat
```

O programa sai em `dist\LivePixListener.exe` e o instalador em `dist\LivePixListener-Setup.exe`.

Para ver o canal no console: `LivePixListener.exe --probe "https://widget.livepix.gg/embed/SEU-ID"`.

## Licença

MIT. Veja [LICENSE](LICENSE).
