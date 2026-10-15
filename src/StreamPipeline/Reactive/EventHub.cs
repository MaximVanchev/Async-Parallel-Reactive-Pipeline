using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace StreamPipeline.Reactive;

/// <summary>
/// Точката, в която паралелният свят „влиза“ в реактивния. Работниците извикват
/// <see cref="Publish"/> от много нишки едновременно, а договорът на Rx изисква
/// сериализирани OnNext извиквания — затова Subject-ът е обвит със Subject.Synchronize.
/// </summary>
public sealed class EventHub : IDisposable
{
    private readonly Subject<PipelineEvent> _subject = new();
    private readonly ISubject<PipelineEvent> _sync;

    public EventHub() => _sync = Subject.Synchronize(_subject);

    public IObservable<PipelineEvent> Events => _sync.AsObservable();

    public void Publish(PipelineEvent e) => _sync.OnNext(e);

    public void Complete() => _sync.OnCompleted();

    public void Fail(Exception error) => _sync.OnError(error);

    public void Dispose() => _subject.Dispose();
}
