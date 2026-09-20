# Refatoração 3: Observer para os eventos de requisição

## 1. Estado atual (Antes da mudança)

Os três motores de requisição chamavam `INotificationHelper` diretamente em cada ponto de decisão de negócio:

- `src/Ombi.Core/Engine/MovieRequestEngine.cs:602,638,743,842,871`
- `src/Ombi.Core/Engine/TvRequestEngine.cs:697,723,745,790,864,1024,1048`
- `src/Ombi.Core/Engine/MusicRequestEngine.cs:345,373,420,482,499`

Exemplo típico (`MovieRequestEngine.cs`, aprovação):

```csharp
var canNotify = await RunSpecificRule(request, SpecificRules.CanSendNotification, string.Empty);
if (canNotify.Success)
{
    await NotificationHelper.Notify(request, NotificationType.RequestApproved);
}
```

A única implementação de `INotificationHelper`, `src/Ombi.Core/Helpers/NotificationHelper.cs`, tratava os 7 métodos do contrato (`NewRequest` x3, `Notify` x4) sempre da mesma forma: montava um `NotificationOptions` e disparava, de forma *hardcoded*, o único canal de saída existente:

```csharp
// repetido nos 7 métodos, ex. linhas 22-25, 37-40, 52-55, 70-73, 85-88, 102-105, 110-113
await OmbiQuartz.TriggerJob(nameof(INotificationService), "Notifications", new Dictionary<string, object>
{
    {JobDataKeys.NotificationOptions, notificationModel}
});
```

Esse `TriggerJob` agenda o job Quartz `INotificationService`/`"Notifications"`, que ao ser executado (`src/Ombi.Notifications/NotificationService.cs:34-43`) itera os 13 agentes de notificação (`INotification`) e dispara cada um. `NotificationHelper` não tinha construtor, nenhum ponto de extensão e nenhum teste unitário próprio (`src/Ombi.Core.Tests` não continha `NotificationHelperTests.cs`).

## 2. Evidência do problema (prova, não opinião)

- **Duplicação literal do canal de saída em 7 lugares**: a mesma chamada `OmbiQuartz.TriggerJob(nameof(INotificationService), "Notifications", ...)` aparecia em `NotificationHelper.cs:22-25, 37-40, 52-55, 70-73, 85-88, 102-105, 110-113` — 7 cópias do mesmo trecho, cada uma precisando ser editada manualmente se o canal de saída mudasse.
- **Zero ponto de extensão**: `NotificationHelper` era instanciada sem construtor (`public class NotificationHelper : INotificationHelper` sem nenhum parâmetro) e chamava `OmbiQuartz` (classe estática) diretamente dentro do corpo dos métodos. Não existia lista, evento ou hook para acoplar um segundo consumidor (ex.: auditoria) sem editar essas 7 chamadas.
- **Impossível testar `NotificationHelper` isoladamente**: busca em `src/Ombi.Core.Tests` não retornou nenhum `NotificationHelperTests.cs` antes desta mudança — a única forma de verificar seu comportamento era mockar `INotificationHelper` inteiro nos testes dos motores (`RequestDeletionNotificationTests.cs`, `MovieRequestEngineTests.cs`), o que testa os motores, não a lógica de publicação em si.
- **Motores acoplados à decisão de "como notificar"**: em todos os 16 pontos de chamada listados na seção 1, a regra de negócio (aprovar/negar/disponibilizar/remover/criar) e o disparo do canal de notificação ocorrem na mesma chamada síncrona, sem uma camada intermediária que permita adicionar um novo assinante (auditoria, métricas) sem tocar no motor ou em `NotificationHelper`.

## 3. Design aplicado

Aplicado o padrão **Observer**: `NotificationHelper` passa a ser o **Subject**, que mantém uma coleção de **Observers** (`IRequestEventObserver`) injetada via DI e notifica todos eles a cada evento, em vez de chamar `OmbiQuartz` diretamente.

**Novo contrato do Observer** — `src/Ombi.Notifications/Interfaces/IRequestEventObserver.cs`:

```csharp
public interface IRequestEventObserver
{
    Task Handle(NotificationOptions notification);
}
```

**Subject** — `src/Ombi.Core/Helpers/NotificationHelper.cs` ganhou um construtor com `IEnumerable<IRequestEventObserver> observers` e um método privado `PublishAsync`, usado pelos 7 métodos do contrato no lugar da chamada direta ao Quartz:

```csharp
public NotificationHelper(IEnumerable<IRequestEventObserver> observers)
{
    _observers = observers;
}

private async Task PublishAsync(NotificationOptions model)
{
    foreach (var observer in _observers)
    {
        await observer.Handle(model);
    }
}
```

**Observer concreto (existente)** — `INotificationService` passou a herdar também de `IRequestEventObserver` (`src/Ombi.Notifications/Interfaces/INotificationService.cs`), e `NotificationService` (`src/Ombi.Notifications/NotificationService.cs`) implementa `Handle` movendo para lá exatamente a chamada que antes estava duplicada em `NotificationHelper`:

```csharp
public async Task Handle(NotificationOptions model)
{
    await OmbiQuartz.TriggerJob(nameof(INotificationService), "Notifications", new Dictionary<string, object>
    {
        {JobDataKeys.NotificationOptions, model}
    });
}
```

**Registro em DI** (`src/Ombi.DependencyInjection/IocExtensions.cs`) — uma linha nova ao lado do registro existente:

```csharp
services.AddTransient<INotificationService, NotificationService>();
services.AddTransient<IRequestEventObserver, NotificationService>();
```

`NotificationHelper` resolve `IEnumerable<IRequestEventObserver>` automaticamente pelo container (`Microsoft.Extensions.DependencyInjection` converte múltiplos `AddTransient` para a mesma interface em uma lista). Não foi necessário nenhum método manual de `Subscribe`/`Attach`.

**Nada mudou** no contrato `INotificationHelper` (`src/Ombi.Core/Senders/INotificationHelper.cs`) nem em nenhuma linha dos três motores de requisição — eles continuam chamando `NotificationHelper.Notify(...)`/`NewRequest(...)` exatamente como antes.

O `IRequestEventObserver` foi colocado em `Ombi.Notifications` (e não em `Ombi.Core`, como um primeiro rascunho do design cogitava) porque `Ombi.Core` já referencia `Ombi.Notifications` como projeto — o inverso criaria uma referência circular entre os dois projetos.

## 4. Benefícios concretos

- **Novo consumidor sem tocar em código existente**: para plugar auditoria ou métricas nos eventos de requisição, basta criar uma classe `IRequestEventObserver` e adicionar `services.AddTransient<IRequestEventObserver, NovoObserver>();` no DI. Nenhuma linha de `NotificationHelper.cs`, `NotificationService.cs` ou dos três motores precisa mudar.
- **Eliminação da duplicação do canal de saída**: a chamada a `OmbiQuartz.TriggerJob` existe em um único lugar (`NotificationService.Handle`) em vez de 7 cópias idênticas espalhadas em `NotificationHelper`.
- **`NotificationHelper` tornou-se testável isoladamente**: antes não tinha construtor nem testes próprios; agora é instanciada com observers mockáveis, permitindo verificar o fan-out (`Notify`/`NewRequest` chamam `Handle` em todos os observers registrados) sem depender do Quartz real. Ver `src/Ombi.Core.Tests/Helpers/NotificationHelperTests.cs` (arquivo novo).
- **Comportamento observável preservado**: o job Quartz `INotificationService`/`"Notifications"` continua sendo disparado da mesma forma, com o mesmo `JobDataMap`, então nenhum agente de notificação (`INotification`, 13 no total) precisou ser alterado.

## 5. Riscos e mudanças de comportamento assumidas

- **Nenhuma mudança de comportamento observável foi introduzida.** A decisão de manter o Quartz como mecanismo de entrega (em vez de os observers serem chamados de forma síncrona e direta) foi confirmada explicitamente antes da implementação, justamente para preservar a fila assíncrona existente e não introduzir efeitos colaterais de threading ou perda de garantias do scheduler.
- **Resolução dupla do mesmo tipo concreto via DI**: `NotificationService` agora está registrado tanto como `INotificationService` (para o Quartz) quanto como `IRequestEventObserver` (para o Subject), ambos `AddTransient`. Isso significa que, quando `NotificationHelper` publica um evento, uma instância de `NotificationService` diferente da que o Quartz eventualmente usará é criada — cada instância roda `PopulateAgents()` (reflection sobre o assembly) em seu construtor. É um custo extra de reflection por chamada, mas sem impacto funcional, pois `NotificationService` não guarda estado compartilhado relevante entre instâncias.
- **Risco de regressão em consumidores futuros de `INotificationService`**: como a interface agora herda de `IRequestEventObserver`, qualquer outra classe que já implementasse `INotificationService` (nenhuma encontrada além de `NotificationService` nesta base de código) passaria a ser obrigada a implementar `Handle` também.

## 6. Plano de testes executado

- **Testes novos**:
  - `src/Ombi.Core.Tests/Helpers/NotificationHelperTests.cs` — 4 testes cobrindo o Subject: fan-out para múltiplos observers, ausência de exceção sem observers registrados, e verificação do payload (`NotificationOptions`) para os três grupos de overload (`Notify(entidade, tipo)`, `NewRequest(entidade)`, `Notify(NotificationOptions)`).
  - `src/Ombi.Notifications.Tests/NotificationServiceHandleTests.cs` — 1 teste verificando que `NotificationService.Handle` dispara o job Quartz correto (`JobKey("INotificationService", "Notifications")`) com o `NotificationOptions` esperado no `JobDataMap`, reutilizando o padrão de test-double `QuartzMock : OmbiQuartz` já existente em `src/Ombi.Schedule.Tests/OmbiQuartzTests.cs`.
- **Testes existentes não alterados**: `RequestDeletionNotificationTests.cs`, `MovieRequestEngineTests.cs` e `V2/MovieRequestEngineTests.cs` continuam mockando `INotificationHelper` diretamente nos motores — não precisaram de nenhuma mudança, confirmando que o contrato consumido pelos motores permaneceu estável.
- **Execução**:
  - `dotnet build src/Ombi.sln` — build completa da solução, 0 erros.
  - `dotnet test src/Ombi.Core.Tests/Ombi.Core.Tests.csproj` — 278 passed, 0 failed, 5 skipped (274 pré-existentes + 4 novos).
  - `dotnet test src/Ombi.Notifications.Tests/Ombi.Notifications.Tests.csproj` — 51 passed, 0 failed (50 pré-existentes + 1 novo).
