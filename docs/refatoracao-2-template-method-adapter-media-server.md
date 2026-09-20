# Refatoração 2: Template Method + Adapter para a sincronização de catálogo dos servidores de mídia

## 1. Estado atual (antes desta mudança)

O ciclo de sincronização de catálogo — buscar o conteúdo do servidor, processar séries, processar filmes e remover o que saiu — existia duplicado, uma vez por servidor de mídia:

- `src/Ombi.Schedule/Jobs/Emby/EmbyContentSync.cs` (371 linhas), apoiado em `EmbyLibrarySync.cs` (155 linhas).
- `src/Ombi.Schedule/Jobs/Jellyfin/JellyfinContentSync.cs` (331 linhas), sem classe base.

Os dois percorriam exatamente a mesma sequência, só que com os tipos trocados:

| Passo | Emby | Jellyfin |
|---|---|---|
| Ler settings e criar cliente | `ISettingsService<EmbySettings>` + `IEmbyApiFactory` | `ISettingsService<JellyfinSettings>` + `IJellyfinApiFactory` |
| Iterar servidores + `try/catch` | `EmbySettings.Servers` | `JellyfinSettings.Servers` |
| Filtrar bibliotecas (`movies`/`tvshows`/`mixed`) | `EmbySelectedLibraries` | `JellyfinSelectedLibraries` |
| Paginar séries | `Api.GetAllShows` → `EmbyItemContainer<EmbySeries>` | `Api.GetAllShows` → `JellyfinItemContainer<JellyfinSeries>` |
| Paginar filmes + expandir `boxset` | `Api.GetAllMovies`/`GetCollection` → `EmbyMovie` | `Api.GetAllMovies`/`GetCollection` → `JellyfinMovie` |
| Buscar registro existente | `_repo.GetByEmbyId` | `_repo.GetByJellyfinId` |
| Gravar identidade | `content.EmbyId` + `EmbyHelper.GetEmbyMediaUrl` | `content.JellyfinId` + `JellyfinHelper.GetJellyfinMediaUrl` |

Não existia nenhuma abstração de "servidor de mídia" no sistema: `IEmbyContentSync` e `IJellyfinContentSync` são interfaces vazias (`: IBaseJob`), e os jobs dependiam diretamente das APIs concretas de cada vendor.

## 2. Evidências do problema (prova, não opinião)

- **Duplicação estrutural do algoritmo**: `ProcessTv` e `ProcessMovies` aparecem nos dois arquivos com o mesmo esqueleto — mesmo laço `while (processed < total)`, mesma paginação por offset, mesma detecção de reidentificação por divergência de `ImdbId`/`TheMovieDbId`/`TvDbId`, mesmo tratamento de `boxset`, mesmo cálculo de `has4K` a partir de `MediaStreams?.FirstOrDefault()?.DisplayTitle`. O mesmo bloco `TODO` sobre `TvRequest.ExternalProviderId` está copiado literalmente em `EmbyContentSync.cs:132-134` e `JellyfinContentSync.cs:136-138`, prova de que os dois arquivos foram editados por cópia.

- **A duplicação já produziu divergência real**: o commit `fd44619d8` (`fix(emby): remove stale records, harden played sync, keep provider-id-less series`) corrigiu apenas o lado Emby. No momento desta refatoração o Jellyfin continuava sem: a guarda de página vazia (`Items == null`) que evita laço infinito, a remoção de registros órfãos (`RemoveStaleContent`), o rastreamento de `syncIncomplete` e a retenção de séries sem `ProviderIds`. Uma correção aplicada em um arquivo não alcançou o outro — o custo concreto da duplicação.

- **Divergências acidentais de parâmetro**: o tamanho de página é `AmountToTake = 300` no Emby e `200` *hardcoded* em quatro chamadas distintas no Jellyfin (`JellyfinContentSync.cs:112,174,187,216`), sem nenhuma justificativa técnica registrada.

- **Ausência de interface de domínio**: uma busca por `IMediaServer*` em `src/Ombi.Schedule/Jobs` não retornava nada antes desta mudança. `MediaServerContent` (entidade) e `IMediaServerContentRepository<T>` (repositório) já existiam em `Ombi.Store`, mas não havia contrapartida na camada de agendamento — o que impedia escrever qualquer passo do ciclo uma única vez.

- **Cobertura assimétrica**: `src/Ombi.Schedule.Tests/EmbyContentSyncTests.cs` cobria 7 cenários do job de Emby; não existia `JellyfinContentSyncTests.cs`. O mesmo algoritmo tinha teste de um lado e nenhum do outro.

- **Código morto herdado da cópia**: `JellyfinContentSync.cs:119-171` envolvia o corpo do laço em um `try { ... } catch (Exception) { throw; }`, que não altera comportamento nenhum, e `JellyfinContentSync.cs:272,278` adicionavam o mesmo item a `toUpdate` duas vezes.

## 3. Design aplicado

Foram aplicados dois padrões combinados, em `src/Ombi.Schedule/Jobs/MediaServer/`.

**Template Method** — `MediaServerContentSync<TContent>` guarda o esqueleto do algoritmo uma única vez. `Execute` fixa a ordem dos passos e delega os pontos que variam:

```csharp
public virtual async Task Execute(IJobExecutionContext context)
{
    OnSyncStarting(context);
    await Catalog.NotifySyncStarting(recentlyAdded);

    var servers = await Catalog.OpenAsync();
    if (servers == null)
    {
        if (RunSyncCompletedWhenDisabled)
        {
            await OnSyncCompleted();
        }
        return;
    }

    await Catalog.NotifySyncStarted();
    foreach (var server in servers) { /* try StartServerCache / catch */ }
    await Catalog.NotifySyncFinished();

    await OnSyncCompleted();
}
```

Ficaram **na base** (escritos uma vez): a orquestração de `Execute`, o `try/catch` por servidor, `ValidateSettings`, a seleção de bibliotecas `movies`/`tvshows`/`mixed`, os dois laços de paginação (`ProcessTv`, `ProcessMovies`), a expansão de `boxset`, a lógica de adicionar/reidentificar série, a de adicionar/atualizar filme, o mapeamento de qualidade/`has4K` e o `Dispose`.

Viraram **ganchos sobrescrevíveis** exatamente os pontos onde Emby e Jellyfin divergem hoje:

| Gancho | Emby | Jellyfin | O que preserva |
|---|---|---|---|
| `StopOnEmptyPage` | `true` | `false` | a guarda de página vazia é do Emby |
| `AddSeriesWithoutProviderIds` | `true` | `false` | Emby adiciona a série, Jellyfin pula |
| `ToleratesMissingProviderIds` | `true` | `false` | leitura defensiva de `ProviderIds` só no Emby |
| `RunSyncCompletedWhenDisabled` | `true` | `false` | com Emby desabilitado, o job de episódios ainda dispara |
| `OnSyncStarting` | lê `recentlyAdded` do `JobDataMap`, limpa ids vistos | *no-op* | modo "recently added" é do Emby |
| `OnContentSeen` | registra o id | *no-op* | rastreamento para limpeza de órfãos |
| `OnSyncCompleted` | `RemoveStaleContent` + gatilhos de episódio e played | gatilho de episódio | pós-processamento distinto |

**Adapter** — `IMediaServerCatalog<TContent>` é a interface de domínio que isola tudo que é específico de cada servidor (API, identidade e notificações):

```csharp
public interface IMediaServerCatalog<TContent> where TContent : MediaServerContent
{
    string ServerName { get; }
    EventId ContentCacherLog { get; }
    int PageSize { get; }

    Task<IReadOnlyList<MediaServerInstance>> OpenAsync();

    Task<MediaServerPage<MediaServerSeries>> GetShows(MediaServerInstance server, string parentId, int startIndex, int count, bool recentlyAdded);
    Task<MediaServerPage<MediaServerMovie>> GetMovies(MediaServerInstance server, string parentId, int startIndex, int count, bool recentlyAdded);
    Task<IReadOnlyList<MediaServerMovie>> GetCollection(MediaServerInstance server, string collectionId);

    Task<TContent> GetByMediaServerId(string mediaServerId);
    string GetMediaServerId(TContent content);
    void SetIdentity(TContent content, string mediaServerId, MediaServerInstance server);

    Task NotifySyncStarting(bool recentlyAdded);
    Task NotifySyncStarted();
    Task NotifySyncFailed();
    Task NotifySyncFinished();
}
```

`EmbyCatalogAdapter` e `JellyfinCatalogAdapter` traduzem os modelos de cada vendor para DTOs neutros (`MediaServerInstance`, `MediaServerLibrary`, `MediaServerPage<T>`, `MediaServerSeries`, `MediaServerMovie`). `BaseProviderids` não precisou de DTO próprio: `EmbyProviderids` e `JellyfinProviderids` já herdavam desse tipo comum em `Ombi.Api.External.Models`.

O par `NotifySyncStarting`/`NotifySyncStarted` existe porque a ordem da notificação difere: o Emby avisa os admins **antes** de checar `Enable`, o Jellyfin **depois**. Cada adaptador implementa só o lado que usa.

`EmbyContentSync` (371 → 139 linhas) e `JellyfinContentSync` (331 → 27 linhas) continuam sendo os pontos de entrada registrados no agendador, com `IEmbyContentSync`/`IJellyfinContentSync` inalteradas. `EmbyLibrarySync` e `EmbyPlayedSync` não foram tocados.

Registro em DI (`src/Ombi.DependencyInjection/IocExtensions.cs`), uma linha por servidor:

```csharp
services.AddTransient<IMediaServerCatalog<Ombi.Store.Entities.EmbyContent>, EmbyCatalogAdapter>();
services.AddTransient<IMediaServerCatalog<Ombi.Store.Entities.JellyfinContent>, JellyfinCatalogAdapter>();
```

## 4. Benefícios concretos

- **O algoritmo passa a existir uma vez**: os ~700 linhas duplicadas entre os dois jobs viraram 416 linhas de esqueleto compartilhado. Uma correção no laço de paginação, na detecção de reidentificação ou no tratamento de `boxset` agora alcança os dois servidores automaticamente — exatamente o que falhou no commit `fd44619d8`.
- **Extensibilidade**: suportar um terceiro servidor de mídia passa a custar um adaptador (~165 linhas, só tradução de tipos) mais duas linhas de DI, em vez de copiar um job de ~350 linhas.
- **As divergências ficaram explícitas e auditáveis**: o que antes era diferença escondida em 700 linhas de código parecido agora são sete ganchos nomeados, visíveis lado a lado na tabela da seção 3. Alinhar Jellyfin com Emby hoje é trocar `false` por `true` num gancho, não reescrever um arquivo.
- **Remoção de código morto**: o `try { } catch (Exception) { throw; }` e o `toUpdate.Add` duplicado do Jellyfin desapareceram junto com o arquivo antigo, sem mudança de comportamento (`toUpdate` é um `HashSet`).
- **A cobertura existente passou a valer para o código compartilhado**: os 7 testes de `EmbyContentSyncTests` agora exercitam `MediaServerContentSync<EmbyContent>`, que é o mesmo esqueleto usado pelo Jellyfin.

## 5. Riscos e mudanças de comportamento assumidas

- **Nenhuma mudança de comportamento observável foi introduzida.** Esta é uma refatoração pura; os bugs conhecidos do lado Jellyfin foram deliberadamente preservados, por decisão explícita confirmada antes da implementação.
- **Bugs do Jellyfin carregados de propósito** (dívida técnica registrada, não corrigida):
  - página vazia com registros restantes continua caindo em `NullReferenceException` em vez de parar o laço (`StopOnEmptyPage = false`);
  - séries sem `ProviderIds` continuam sendo puladas em vez de adicionadas (`AddSeriesWithoutProviderIds = false`);
  - `ProviderIds` nulo continua lançando `NullReferenceException` em vez de ser tratado (`ToleratesMissingProviderIds = false`);
  - o Jellyfin continua sem remoção de registros órfãos.
  Cada um desses pontos é hoje uma linha só: trocar o gancho para `true` no `JellyfinContentSync` (e implementar `RemoveStaleContent`) alinha o Jellyfin com o Emby. Isso ficou fora do escopo por ser correção de bug, não refatoração.
- **Contagem total de linhas subiu** (702 → 1004 somando base, interface, DTOs e adaptadores). O ganho é de reuso e ponto único de manutenção, não de volume: o *algoritmo* deixou de estar duplicado, mas a tradução de tipos por vendor é código novo e explícito.
- **`EmbyLibrarySync` continua existindo** para servir `EmbyPlayedSync`, que está fora do escopo desta tarefa. Isso mantém o laço de iteração de servidores duplicado entre `EmbyLibrarySync` e `MediaServerContentSync`. Migrar `EmbyPlayedSync` para a nova base é o próximo passo natural.
- **Uma linha de teste mudou**: `EmbyContentSyncTests.Setup` passou a injetar o adaptador real via `_mocker.Use<IMediaServerCatalog<EmbyContent>>(...)`, porque o `AutoMocker` criaria um mock vazio da nova dependência. Os mocks de `IEmbyApi`, `IEmbyContentRepository`, `ISettingsService<EmbySettings>` e `INotificationHubService` e todas as asserções permaneceram idênticos.

## 6. Plano de testes executado

Ambiente: a máquina não tinha .NET SDK instalado (só runtimes) nem `yarn`, que `src/Ombi/Ombi.csproj` exige no build. Foram instalados o SDK 8.0.425 e o `yarn` antes de medir a linha de base — sem isso, `Ombi.Schedule.Tests` nem chegava a ser executado.

**Linha de base** (`dotnet test src/Ombi.sln`, antes da refatoração):

| Projeto | Resultado |
|---|---|
| Ombi.Helpers.Tests | 152 aprovados |
| Ombi.Settings.Tests | 6 aprovados |
| Ombi.Notifications.Tests | 45 aprovados, **6 com falha** |
| Ombi.Tests | 18 aprovados |
| Ombi.Schedule.Tests | 131 aprovados |
| Ombi.Api.IntegrationTests | 58 aprovados |
| Ombi.Core.Tests | 308 aprovados, 5 ignorados |

As 6 falhas são **pré-existentes** e sem relação com esta mudança: todas em `NotificationMessageCurlysTests` (`IssueNotificationTests_NoRequest`, `MovieIssueNotificationTests`, `MovieNotificationTests`, `MusicNotificationTests`, `TvNotificationPartialAvailablilityTests`, `TvNotificationTests`), em um projeto que esta refatoração não toca.

**Depois da refatoração**: resultado idêntico em todos os sete projetos, com exatamente as mesmas 6 falhas pré-existentes e os mesmos nomes de teste. Nenhuma falha nova introduzida, nenhum teste novo necessário — a suíte existente de `Ombi.Schedule.Tests` (131 testes, incluindo os 7 de `EmbyContentSyncTests`) passou sem alteração de asserções.

Os 7 cenários de `EmbyContentSyncTests` que validam o esqueleto compartilhado: parada em página vazia no TV e no filme, série sem `ProviderIds` adicionada só com o id do Emby, série que perdeu os ids sem sobrescrever os existentes, remoção de conteúdo ausente no sync completo, ausência de remoção no modo *recently added*, e supressão da limpeza quando um servidor falha.
