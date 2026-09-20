# Diagnóstico: Refatoração de TvSender/MovieSender (Strategy + Factory)

## 1. Estado atual (antes desta mudança)

O envio de uma requisição aprovada para o gerenciador externo correto era feito por dois métodos monolíticos, `TvSender.Send` e `MovieSender.Send`, que testavam cada gerenciador em sequência através de cascatas `if/else` hardcoded:

- **TV**: Sonarr → SickRage (`TvSender.cs`, então com 634 linhas).
- **Filme**: Radarr → DogNzb → CouchPotato, com um fork extra `is4K` dentro do próprio Radarr (`MovieSender.cs`, então com 295 linhas).

Os construtores recebiam as APIs concretas de cada gerenciador diretamente:

| Classe | Dependências no construtor |
|---|---|
| `TvSender` | `ISonarrV3Api`, `ISettingsService<SonarrSettings>`, `ISickRageApi`, `ISettingsService<SickRageSettings>`, `IRepository<UserQualityProfiles>`, `IRepository<RequestQueue>`, `INotificationHelper`, `ILogger<TvSender>` — 8 no total |
| `MovieSender` | `ISettingsService<RadarrSettings>`, `ISettingsService<Radarr4KSettings>`, `IDogNzbApi`, `ISettingsService<DogNzbSettings>`, `ICouchPotatoApi`, `ISettingsService<CouchPotatoSettings>`, `IRadarrV3Api`, `IRepository<UserQualityProfiles>`, `IRepository<RequestQueue>`, `INotificationHelper`, `ILogger<MovieSender>` — 11 no total |

Ou seja, cada classe conhecia todos os seus destinos em tempo de compilação — clássica violação do Open/Closed Principle: adicionar um novo gerenciador exigia editar `Send` e o construtor diretamente.

## 2. Evidências do problema (prova, não opinião)

- **Zero cobertura de teste**: antes desta mudança não existia `TvSenderTests.cs` nem `MovieSenderTests.cs` em `src/Ombi.Core.Tests/Senders/` — a única classe de sender testada era `MassEmailSender`. Em `MovieRequestEngineTests.cs`, `IMovieSender` só aparecia como `Mock<IMovieSender>` totalmente opaco; a lógica de despacho (634 + 295 linhas) nunca era exercitada por um teste unitário.

- **Bug real de comportamento**: em `MovieSender.cs:80-85` (código antigo), o resultado de `SendToDogNzb` (`Task<DogNzbMovieAddResult>`) era descartado e o `Send` forçava `Success = true, Sent = true` incondicionalmente:
  ```csharp
  await SendToDogNzb(model, dogSettings);
  result = new SenderResult { Success = true, Sent = true };
  ```
  Uma falha real no DogNzb era reportada ao usuário como sucesso.

- **Contratos de retorno assimétricos**: `SendToSonarr` era `public`, retornava `Task<NewSeries>` (um DTO específico do Sonarr) e relançava exceções. `SendToSickRage` era `private`, retornava `Task<bool>` e engolia a maioria das falhas (havia um `// Do something` deixado como TODO em vez de tratamento real). Do lado de filme, `SendToRadarr` retornava `SenderResult`, `SendToDogNzb` retornava `DogNzbMovieAddResult`, `SendToCp` retornava `SenderResult` construído de um `bool` cru — quatro formatos diferentes que `Send` reconciliava manualmente em cada branch.

- **Fallback invertido**: uma exceção real do Sonarr abortava a tentativa do SickRage (havia um único `catch` global envolvendo os dois blocos), mas um `ApiKey` vazio no Sonarr deixava cair silenciosamente para o SickRage — ou seja, o caso "mal configurado" tinha fallback, e o caso "erro real de rede/API" não.

- **Duplicação de código**: `AddToRequestFailureQueue` estava copiado quase verbatim entre `TvSender.cs` e `MovieSender.cs`, diferindo só em `RequestType.TvShow` vs `RequestType.Movie`.

- **Vazamento de conceito interno na abstração pública**: `IMovieSender.Send(MovieRequests model, bool is4K)` expunha que "Radarr tem uma instância 4K" na interface pública, que deveria ser agnóstica de backend.

## 3. Design aplicado

Cada gerenciador externo virou uma estratégia isolada e testável, implementando uma interface comum resolvida por uma fábrica em ordem de prioridade. `TvSender`/`MovieSender` passaram a ser orquestradores finos, sem conhecer os detalhes de protocolo de nenhum vendor.

```csharp
public interface ITvDvrSender
{
    int Priority { get; }
    Task<bool> IsEnabledAsync();
    Task<SenderResult> Send(ChildRequests model);
}
public interface ITvDvrSenderFactory
{
    Task<IReadOnlyList<ITvDvrSender>> GetEnabledSendersAsync();
}
```

Estratégias criadas em `src/Ombi.Core/Senders/Dvr/`:

- `SonarrDvrSender` (Priority 0) e `SickRageDvrSender` (Priority 1) para TV.
- `RadarrDvrSenderBase` (abstrata) com duas subclasses concretas, `RadarrDvrSender` e `Radarr4KDvrSender`, cada uma decidindo sozinha sua elegibilidade via `IsEnabledAsync(bool is4K)`. Isso tira o `is4K` de dentro do método de envio sem duplicar a lógica de tags/root-path/quality-profile (compartilhada na classe base).
- `DogNzbDvrSender` e `CouchPotatoDvrSender` completam a cadeia de filme.

`TvDvrSenderFactory`/`MovieDvrSenderFactory` resolvem `IEnumerable<ITvDvrSender>`/`IEnumerable<IMovieDvrSender>` via DI, ordenam por `Priority` e filtram pelas estratégias habilitadas — a ordenação não depende da ordem de registro no container.

`TvSender`/`MovieSender` percorrem a lista de estratégias habilitadas, tentam cada uma em ordem e isolam exceções por estratégia (uma falha no Sonarr não impede mais a tentativa do SickRage — mudança de comportamento intencional, coberta por teste dedicado). As assinaturas públicas `ITvSender.Send(ChildRequests)` e `IMovieSender.Send(MovieRequests, bool is4K)` não mudaram, então `TvRequestEngine`, `MovieRequestEngine` e `ResendFailedRequests` não precisaram de alterações.

## 4. Benefícios concretos

- **Extensibilidade**: um novo gerenciador (ex: NZBGet) passa a ser uma nova classe implementando `ITvDvrSender`/`IMovieDvrSender` + uma linha de registro no DI — zero mudança em `TvSender`/`MovieSender`.
- **Testabilidade**: 0 → 35 testes novos cobrindo os orquestradores (fallback, isolamento de exceção, fila de falha), as fábricas (ordenação por prioridade, filtragem por habilitação) e a elegibilidade de cada estratégia (`IsEnabledAsync`), algo impossível de testar isoladamente no monólito anterior.
- **Correção do bug do DogNzb**: `DogNzbDvrSender.Send` agora reflete o resultado real da chamada à API em vez de forçar sucesso, com teste de regressão dedicado (`DogNzbDvrSenderTests.Send_ApiReturnsNull_ReportsFailure_InsteadOfForcingSuccess`).
- **Contratos consistentes**: toda estratégia retorna `SenderResult`; não há mais reconciliação manual de 4 formatos de retorno diferentes.
- **Isolamento de falha por estratégia**: uma exceção em um gerenciador não bloqueia mais a tentativa do próximo da lista (corrige o fallback invertido descrito acima).

## 5. Riscos e mudanças de comportamento assumidas

- **Fallback por exceção isolada**: antes, uma exceção no Sonarr abortava a tentativa do SickRage; agora a próxima estratégia é sempre tentada. Coberto por `TvSenderTests.Send_FirstStrategyThrows_SecondIsStillAttempted`.
- **Predicate de sucesso do DogNzb**: a API (`AddMovie`) retorna um XML de RSS sem campo explícito de sucesso/falha; a ausência de um valor não-nulo é o único sinal disponível. Recomenda-se validar esse comportamento com uma instância real de DogNzb antes do deploy em produção.
- Ficaram fora de escopo desta refatoração (débito técnico anotado, não corrigido): a duplicação de `AddToRequestFailureQueue` entre `TvSender`/`MovieSender`, um campo em `SenderResult` indicando qual estratégia respondeu, e o TODO `// Do something` dentro da lógica do SickRage.

## 6. Plano de testes executado

- `TvSenderTests.cs` / `MovieSenderTests.cs` — nenhuma estratégia habilitada; primeira sucede (segunda nunca é chamada); primeira falha e segunda sucede (fallback); exceção de uma estratégia não aborta a próxima; todas falham → grava na fila de falha.
- `Dvr/TvDvrSenderFactoryTests.cs` / `Dvr/MovieDvrSenderFactoryTests.cs` — ordenação por `Priority` e filtragem por `IsEnabledAsync`.
- `Dvr/SonarrDvrSenderTests.cs` / `Dvr/SickRageDvrSenderTests.cs` — elegibilidade (`IsEnabledAsync`), incluindo o caso "Enabled mas sem ApiKey" absorvido do comportamento antigo do Sonarr.
- `Dvr/RadarrDvrSenderTests.cs` — elegibilidade cruzada entre `RadarrDvrSender`/`Radarr4KDvrSender` conforme o parâmetro `is4K`.
- `Dvr/DogNzbDvrSenderTests.cs` — prova da correção do bug de resultado descartado.
- `Dvr/CouchPotatoDvrSenderTests.cs` — caminho feliz de envio.

Toda a suíte `Ombi.Core.Tests` (304 testes) foi executada após a migração e passou sem regressões.
