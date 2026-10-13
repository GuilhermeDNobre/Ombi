# Ombi — Refatoração com Padrões de Projeto

Fork do [Ombi](https://github.com/Ombi-app/Ombi) usado como objeto de estudo no Trabalho 1 da
disciplina **QXD0068 — Reuso de Software**, da Universidade Federal do Ceará, Campus Quixadá,
ministrada pelo Prof. Francisco Victor da Silva Pinheiro.

O objetivo foi identificar problemas de projeto em um sistema legado real e corrigi-los com
padrões de projeto GoF, preservando o comportamento original.

## Integrantes

- Guilherme Damasceno Nobre — [@GuilhermeDNobre](https://github.com/GuilhermeDNobre)
- Vitor Manoel Alves da Silva — [@aSTRonuun](https://github.com/aSTRonuun)

## Relatório técnico

O relatório completo está em [`docs/relatorio-tecnico.pdf`](docs/relatorio-tecnico.pdf). Ele traz
o diagnóstico do sistema, a justificativa de cada padrão, os trechos de código antes e depois,
a validação por testes e a análise dos resultados.

## O sistema

O Ombi é uma aplicação web de código aberto que funciona como camada de pedidos para servidores
de mídia pessoais, como Plex, Emby e Jellyfin. Ele não armazena nem reproduz vídeo: o que faz é
organizar pedidos e conversar com os outros sistemas.

O ciclo é este:

1. O usuário pesquisa um filme ou série no catálogo do TheMovieDb
2. O pedido fica registrado como pendente
3. O administrador aprova ou recusa
4. O pedido aprovado é enviado a um gerenciador de downloads, como Sonarr ou Radarr
5. Quando o conteúdo aparece no servidor de mídia, o usuário é notificado

O projeto foi escolhido a partir do [CodeTriage](https://www.codetriage.com). São 23 projetos na
solution, dos quais 7 são de teste automatizado — o que permitiu comparar o comportamento antes e
depois de cada refatoração.

### Tecnologias

| Camada | Tecnologia |
|---|---|
| Back-end | C# sobre .NET 8 |
| Persistência | Entity Framework Core com SQLite |
| Tarefas agendadas | Quartz.NET |
| Front-end | Angular |
| Testes | NUnit e Moq |

## Versões no repositório

O enunciado pede que a versão original e a refatorada sejam distinguíveis. A organização é esta:

| Referência | Conteúdo |
|---|---|
| tag `v-original` | Versão original do sistema, no commit `c564409`, exatamente como estava no upstream quando o fork foi criado |
| branch `refactored` | Versão final refatorada |
| branch `develop` | Branch de integração onde as refatorações foram mescladas |
| branches `refact*` | Uma por refatoração, preservadas para acompanhar a evolução |

Para ver tudo o que mudou entre as duas versões:

```bash
git diff v-original..refactored --stat
```

## Como executar

### Pré-requisitos

- [.NET SDK 8](https://dotnet.microsoft.com/download) — a versão exata está fixada em `global.json`
- [Node.js 20 ou superior](https://nodejs.org) e Yarn, para compilar o front-end

Não é preciso instalar banco de dados. O SQLite é criado automaticamente na primeira execução.

### Passo a passo

```bash
git clone https://github.com/GuilhermeDNobre/Ombi.git
cd Ombi
git switch refactored

# front-end (uma vez só)
cd src/Ombi/ClientApp
yarn install
yarn build
cd ../../..

# aplicação
dotnet run --project src/Ombi/Ombi.csproj -c Release
```

A aplicação sobe em **http://localhost:3577**. Na primeira execução aparece o assistente de
configuração, onde se define o usuário administrador. As integrações com servidor de mídia e
gerenciador de downloads podem ser puladas.

> Use `-c Release`. Em compilação Debug o projeto repassa a interface para o servidor de
> desenvolvimento do Angular na porta 3578, e a tela não carrega se ele não estiver rodando.

### Modo de desenvolvimento

Para trabalhar no front-end com recarga automática, são dois processos:

```bash
# terminal 1
dotnet run --project src/Ombi/Ombi.csproj

# terminal 2
cd src/Ombi/ClientApp
yarn start
```

A interface fica em http://localhost:3578 e repassa as chamadas de API para a 3577.

### Testes

```bash
dotnet test src/Ombi.sln
```

## O que foi refatorado

Foram aplicados seis padrões GoF, cobrindo as três categorias do catálogo. No total, 62 arquivos
alterados, com 4.009 linhas adicionadas e 1.935 removidas, e 35 classes de produção novas.

### Strategy e Factory Method — envio aos gerenciadores de downloads

`TvSender` e `MovieSender` verificavam cada gerenciador suportado em blocos `if` sucessivos e
recebiam as APIs concretas (`ISonarrV3Api`, `ISickRageApi` e outras) diretamente no construtor.
Incluir um gerenciador novo exigia editar essas classes.

Cada envio virou uma estratégia em `src/Ombi.Core/Senders/Dvr`, implementando `ITvDvrSender` ou
`IMovieDvrSender`. As fábricas `TvDvrSenderFactory` e `MovieDvrSenderFactory` devolvem apenas as
estratégias habilitadas, na ordem de prioridade. O comportamento comum entre Radarr e Radarr 4K
ficou em `RadarrDvrSenderBase`.

- `TvSender.cs`: 633 → 83 linhas
- `MovieSender.cs`: 294 → 83 linhas

### Template Method e Adapter — sincronização dos servidores de mídia

Plex, Emby e Jellyfin tinham jobs independentes executando o mesmo algoritmo de sincronização de
catálogo, sem nenhuma abstração comum de servidor de mídia.

A classe abstrata `MediaServerContentSync` passou a conter o esqueleto do algoritmo, e a interface
`IMediaServerCatalog<TContent>` define o que o domínio precisa de um servidor. `EmbyCatalogAdapter`
e `JellyfinCatalogAdapter` traduzem cada API para os modelos comuns `MediaServerMovie`,
`MediaServerSeries`, `MediaServerLibrary` e `MediaServerPage`.

- `JellyfinContentSync.cs`: 331 → 27 linhas
- `EmbyContentSync.cs`: 371 → 139 linhas

O job do Plex não foi migrado, por ter comportamentos próprios que exigiriam pontos de extensão
adicionais na classe base.

### Observer — eventos de requisição

O `NotificationHelper` acionava o agendador do Quartz dentro de cada um dos seus sete métodos, o
que amarrava a regra de negócio ao mecanismo de entrega da notificação.

Foi criada a interface `IRequestEventObserver`, com o método `Handle`. O helper passou a receber a
coleção de observadores pelo construtor e a publicar os eventos. O `NotificationService` passou a
implementar `IRequestEventObserver` e assumiu o disparo do job. A ligação entre os dois é o
registro em `IocExtensions.cs`.

### Facade — motor de pedidos de filmes

`MovieRequestEngine` acumulava validação, checagem de duplicidade, regras de aprovação, controle de
cota, persistência, notificação, envio ao gerenciador e montagem dos modelos de exibição, além de
quatro blocos `switch` de filtro e ordenação.

As responsabilidades foram extraídas para cinco serviços em `src/Ombi.Core/Engine/Requests`:
`MovieRequestFactory`, `MovieRequestStatusService`, `MovieRequestDispatcher`, `MovieRequestEnricher`
e `MovieRequestQueryBuilder`. A classe permaneceu no lugar como fachada, com a mesma interface
pública `IMovieRequestEngine`, de modo que nenhum controller precisou ser alterado.

- `MovieRequestEngine.cs`: 887 → 430 linhas

O `TvRequestEngine` não foi refatorado e continua com 1.095 linhas.

## Validação

A suíte de testes do próprio projeto foi executada na tag `v-original` e na versão refatorada:

| Versão | Aprovados | Falhas | Ignorados | Total |
|---|---|---|---|---|
| Original (`v-original`) | 683 | 6 | 5 | 694 |
| Refatorada | 718 | 6 | 5 | 729 |

As 6 falhas são as mesmas nas duas execuções, todas em `NotificationMessageCurlysTests`, e decorrem
da cultura configurada na máquina de teste: os testes comparam textos traduzidos com os valores em
inglês. São anteriores à refatoração.

Os 35 testes a mais vêm de 11 arquivos de teste novos, escritos sobre as classes extraídas. As
telas principais também foram verificadas com testes de ponta a ponta usando Playwright.

## Licença

O Ombi é distribuído sob a licença **GPL-2.0**. Este fork mantém a mesma licença e os avisos de
copyright do projeto original. Veja [LICENSE](LICENSE).
