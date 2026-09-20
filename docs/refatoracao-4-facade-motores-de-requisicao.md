# Refatoração 4: Facade para o motor de requisição de filmes

## 1. Estado atual (antes desta mudança)

`src/Ombi.Core/Engine/MovieRequestEngine.cs` tinha 887 linhas e acumulava, numa única classe, todas as etapas do ciclo de vida de uma requisição de filme:

| Responsabilidade | Linhas (antes) | Tamanho |
|---|---|---|
| A — Busca no TheMovieDb, validação, permissões (`RequestOnBehalf`, overrides), gate de 4K | 66–107 | 42 |
| B — Detecção de duplicidade / merge com requisição existente + montagem da entidade | 109–158 | 50 |
| C — Avaliação de regras + orquestração de auto-aprovação | 160–194 | 35 |
| D — `switch` de filtro e ordenação | 223, 237, 317, 422–453 | ~95 |
| E — Preâmbulo `HideFromOtherUsers` + `GetWithUser`, repetido **8 vezes** | 8 locais | ~80 |
| F — Enriquecimento de view model (assinatura + assistido) | 499–545 | 47 |
| G — Aprovar / negar / marcar disponível / indisponível | 571–643, 793–851 | 132 |
| H — Envio ao gerenciador externo | 670–701, 778–791 | 46 |
| I — Persistência + notificação + log de requisição | 853–885 | 33 |
| J — Requisição de coleção | 645–668 | 24 |
| K — CRUD diverso | 399–420, 708–776 | 90 |

O construtor recebia 15 dependências. `TvRequestEngine` (1095 linhas) e `MusicRequestEngine` (606) seguem o mesmo padrão.

## 2. Evidências do problema (prova, não opinião)

- **Uma classe, onze responsabilidades**: a tabela da seção 1 lista onze blocos coesos e independentes convivendo no mesmo arquivo. `RequestMovie` sozinho (66–194, 129 linhas) fazia chamada de API externa, verificação de permissão, decisão de duplicidade, construção de entidade, avaliação de regra e orquestração de aprovação.

- **Duplicação do preâmbulo de consulta**: o bloco

  ```csharp
  var shouldHide = await HideFromOtherUsers();
  IQueryable<MovieRequests> allRequests;
  if (shouldHide.Hide) { allRequests = MovieRepository.GetWithUser(shouldHide.UserId); }
  else { allRequests = MovieRepository.GetWithUser(); }
  ```

  aparecia **8 vezes** no mesmo arquivo (`GetRequests` x2, `GetRequestsByStatus`, `GetUnavailableRequests`, `GetTotal`, `GetRequests()`, `SearchMovieRequest`), variando só no sufixo.

- **`switch` de filtro/ordenação repetidos entre os três motores**: `ApplySortMovies` (`MovieRequestEngine.cs:422`), `ApplySortTv` (`TvRequestEngine.cs:543`) e `ApplySortAlbums` (`MusicRequestEngine.cs:593`) são três versões quase idênticas do mesmo `switch`. O `switch (status)` sobre `RequestStatus` existe nos três (`Movie:317`, `Tv:423`, `Music:532`), e `OrderMovies`/`OrderAlbums` são duas cópias do mesmo `switch` sobre `OrderType`.

- **Cobertura concentrada e assimétrica**: `src/Ombi.Core.Tests/Engine/MovieRequestEngineTests.cs` tem 20 testes, e **18 deles** cobrem apenas `GetRequestsByStatus`. `RequestMovie` — o caminho mais complexo do arquivo, com 129 linhas e 5 pontos de saída antecipada — **não tinha nenhum teste**. `src/Ombi.Core.Tests/Engine/V2/MovieRequestEngineTests.cs` tem um único teste, marcado `[Ignore("Needs to be tested")]`, que nunca executa.

- **Construtor com 15 dependências**: sintoma direto do número de responsabilidades. O teste V2 precisava montar 15 mocks manualmente só para instanciar a classe.

- **Dependências mortas escondidas pelo tamanho**: após extrair as responsabilidades, `IRepository<RequestLog>` e `IFeatureService` ficaram sem nenhum uso no motor — eram usados só por `AddMovieRequest` e pelo gate de 4K. Em um arquivo de 887 linhas isso passava despercebido.

## 3. Design aplicado

Aplicado o padrão **Facade**: `MovieRequestEngine` continua sendo a única porta de entrada para os controllers, mas deixou de implementar as etapas e passou a orquestrar colaboradores coesos, todos em `src/Ombi.Core/Engine/Requests/`.

| Serviço | Absorve | Dependências |
|---|---|---|
| `IMovieRequestQueryBuilder` | D | nenhuma (transformações puras de `IQueryable`) |
| `IMovieRequestEnricher` | F | `IRepository<RequestSubscription>`, `IUserPlayedMovieRepository` |
| `IMovieRequestDispatcher` | H | `IMovieSender`, `ILogger` |
| `IMovieRequestStatusService` | G | `IRequestServiceMain`, `INotificationHelper`, `IRuleEvaluator`, `IMediaCacheService`, `IMovieRequestDispatcher` |
| `IMovieRequestFactory` | A + B + I | `IMovieDbApi`, `IRequestServiceMain`, `ICurrentUser`, `OmbiUserManager`, `IFeatureService`, `INotificationHelper`, `IRuleEvaluator`, `IMediaCacheService`, `IRepository<RequestLog>` |

A responsabilidade **E** virou um método privado `LoadRequests(HideResult)` no próprio motor — é duplicação local, não um serviço:

```csharp
private IQueryable<MovieRequests> LoadRequests(HideResult shouldHide)
{
    return shouldHide.Hide
        ? MovieRepository.GetWithUser(shouldHide.UserId)
        : MovieRepository.GetWithUser();
}
```

`RequestMovie` passou de 129 para 45 linhas e agora expressa só a sequência, com a construção delegada ao factory e a regra de negócio (**C**) mantida na fachada:

```csharp
var buildResult = await _factory.Build(model);
if (buildResult.Error != null)
{
    return buildResult.Error;
}

var requestModel = buildResult.Request;

var ruleResults = (await RunRequestRules(requestModel)).ToList();
```

`MovieRequestBuildResult` carrega ou o erro de validação/permissão, ou a entidade construída mais os metadados (`FullMovieName`, `IsExisting`, `Is4kRequest`) que o motor precisa para decidir o caminho de auto-aprovação.

Os métodos públicos que sobraram são fachada pura:

```csharp
public async Task<RequestEngineResult> ApproveMovie(MovieRequests request, bool is4K)
{
    return await _statusService.Approve(request, is4K);
}
```

**`IMovieRequestEngine` não mudou nenhuma assinatura**, então nenhum controller em `Ombi.Controllers` precisou ser tocado. `TvRequestEngine` e `MusicRequestEngine` não foram alterados — os serviços foram desenhados para que possam adotá-los depois, mas isso está fora do escopo desta passagem.

Registro em DI (`src/Ombi.DependencyInjection/IocExtensions.cs`), cinco linhas ao lado do registro existente do motor.

## 4. Benefícios concretos

- **Motor 51% menor**: 887 → 430 linhas. O construtor caiu de 15 para 14 parâmetros, mas 5 deles agora são colaboradores coesos no lugar de 6 dependências de infraestrutura de baixo nível; `IRepository<RequestLog>` e `IFeatureService` saíram por terem ficado sem uso.
- **`RequestMovie` legível**: 129 → 45 linhas, com a sequência de etapas visível de uma vez só, sem a montagem de entidade de 25 campos no meio.
- **Unidades testáveis isoladamente**: `MovieRequestQueryBuilder` não tem nenhuma dependência e pode ser testado com listas em memória; `MovieRequestDispatcher` precisa só de dois mocks, contra os 15 exigidos hoje para instanciar o motor. A lógica de `RequestMovie`, antes impossível de testar sem montar o motor inteiro, agora é alcançável via `IMovieRequestFactory` com 9 mocks.
- **Duplicação local eliminada**: o preâmbulo de 8 linhas repetido 8 vezes virou uma chamada de uma linha em cada um dos 7 pontos que o usavam.
- **Caminho aberto para os outros motores**: `TvRequestEngine` e `MusicRequestEngine` podem adotar os mesmos cinco serviços; os `switch` de ordenação duplicados nos três arquivos passam a ter um destino natural.

## 5. Riscos e mudanças de comportamento assumidas

- **Nenhuma mudança de comportamento observável foi introduzida.** Todo código movido foi transportado literalmente, incluindo mensagens de erro, ordem dos `await`, tipos de exceção (`ArgumentOutOfRangeException` sem parâmetros nos filtros, e com `nameof(type)` em `Order`) e o `default: break` do `switch` de `RequestStatus`, que mantém a consulta inalterada em vez de lançar.
- **Categoria de log preservada deliberadamente**: `MovieRequestDispatcher` recebe `ILogger<MovieRequestEngine>`, e não `ILogger<MovieRequestDispatcher>`. A mensagem `"Tried auto sending movie but failed"` é emitida tanto pelo dispatcher quanto pelo motor; usar a categoria natural do serviço mudaria o nome da categoria nos logs para metade dos casos. Há precedente no repositório (`EmbyLibrarySync` recebe `ILogger<EmbyContentSync>`). Um revisor que considere a categoria irrelevante pode trocar por `ILogger<MovieRequestDispatcher>` em uma linha.
- **`MovieRequestFactory` é o ponto de maior risco**: absorve `RequestMovie`, que não possui nenhum teste unitário. A garantia aqui é apenas a transposição literal do código e a compilação; os 20 testes de `MovieRequestEngineTests` não exercitam esse caminho.
- **Duas linhas por serviço nos testes existentes**: `MovieRequestEngineTests.Setup` passou a injetar as implementações reais via `_mocker.Use<...>`, porque o `AutoMocker` criaria mocks vazios das novas dependências e os 18 testes de `GetRequestsByStatus` passariam a exercitar nada. `V2/MovieRequestEngineTests` constrói o motor posicionalmente e precisou acompanhar cada mudança de construtor. Nenhuma asserção foi alterada em nenhum dos dois arquivos.
- **`ReProcessRequest` mantém o dispatcher direto**: o motor injeta `IMovieRequestDispatcher` além de `IMovieRequestStatusService`, porque reenviar uma requisição existente não passa por transição de status. Alternativa seria expor `ReProcess` no status service, o que misturaria as duas responsabilidades.
- **Escopo**: apenas `MovieRequestEngine`. `TvRequestEngine` (1095 linhas) e `MusicRequestEngine` (606) permanecem intactos, assim como `BaseMediaEngine` e as interfaces em `Engine/Interfaces`.

## 6. Plano de testes executado

**Linha de base** (`dotnet test src/Ombi.sln`, antes da refatoração):

| Projeto | Resultado |
|---|---|
| Ombi.Helpers.Tests | 152 aprovados |
| Ombi.Settings.Tests | 6 aprovados |
| Ombi.Notifications.Tests | 45 aprovados, **6 com falha** |
| Ombi.Tests | 18 aprovados |
| Ombi.Schedule.Tests | 131 aprovados |
| Ombi.Api.IntegrationTests | 58 aprovados |
| **Ombi.Core.Tests** | **308 aprovados, 5 ignorados** |

As 6 falhas são pré-existentes, todas em `NotificationMessageCurlysTests`, em um projeto que esta refatoração não toca.

**Execução por commit**: `dotnet test src/Ombi.Core.Tests/Ombi.Core.Tests.csproj` foi executado após cada um dos seis commits, sempre com o mesmo resultado — 308 aprovados, 0 com falha, 5 ignorados:

| Commit | Motor após |
|---|---|
| `refactor: extract the movie request loading preamble into a helper` | 823 linhas |
| `refactor: extract movie request filtering and sorting into a query builder` | 738 |
| `refactor: extract movie request view model enrichment into a service` | 690 |
| `refactor: extract movie request sending into a dispatcher service` | 658 |
| `refactor: extract movie request approval and availability into a status service` | 552 |
| `refactor: extract movie request creation and persistence into a factory` | 430 |

**Depois da refatoração**: `dotnet test src/Ombi.sln` com resultado idêntico ao da linha de base em todos os sete projetos, com exatamente as mesmas 6 falhas pré-existentes. Nenhuma falha nova introduzida.
