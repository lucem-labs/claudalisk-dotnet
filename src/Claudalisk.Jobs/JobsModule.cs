using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;

namespace Claudalisk.Jobs;

public enum JobKindType
{
    Command,
    Batch,
    Event,
    Rebuild,
}

public sealed record JobKind(string Name, JobKindType Type, Type PayloadType, Type? HandlerType, Type? CheckpointType, string? Lane = null, string? EventKind = null);

public sealed record EventSubscription(string Module, string Kind, Type EventType, Type HandlerType, string Lane = JobOptions.DefaultLane);

public sealed partial class JobsModule
{
    private readonly Dictionary<string, JobKind> _kinds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _lanes = new(StringComparer.Ordinal) { [JobOptions.DefaultLane] = 1 };

    internal JobsModule(string name, string schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Identifier().IsMatch(schema ?? ""))
        {
            throw new ArgumentException($"A jobs schema is a lower-case identifier; got '{schema}'.", nameof(schema));
        }

        Name = name;
        Schema = schema!;
        Segment = Claudalisk.AspNetCore.NameSegments.Kebab(name);
    }

    public string Name { get; }

    public string Schema { get; }

    public string Segment { get; }

    public IReadOnlyDictionary<string, int> Lanes => _lanes;

    public TimeSpan? Retention { get; private set; }

    internal bool LibraryOwned { get; set; }

    public IReadOnlyDictionary<string, JobKind> Kinds => _kinds;

    public JobKind? KindFor(Type payloadType) =>
        _kinds.Values.FirstOrDefault(kind => kind.Type != JobKindType.Event && kind.PayloadType == payloadType);

    internal void AddLane(string lane, int workers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lane);
        ArgumentOutOfRangeException.ThrowIfNegative(workers);
        if (workers > 1 && _kinds.Values.FirstOrDefault(kind => kind.Type == JobKindType.Rebuild && kind.Lane == lane) is { } rebuild)
        {
            throw new InvalidOperationException($"Lane '{lane}' of module '{Name}' runs the rebuild '{rebuild.Name}', so it has one worker at most.");
        }

        _lanes[lane] = workers;
    }

    internal void SetRetention(TimeSpan retention)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention, TimeSpan.Zero);
        Retention = retention;
    }

    internal void AddKind(JobKind kind)
    {
        if (_kinds.TryGetValue(kind.Name, out var existing) && existing != kind)
        {
            throw new InvalidOperationException($"The jobs of module '{Name}' already have a kind named '{kind.Name}'.");
        }

        _kinds[kind.Name] = kind;
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]*$")]
    private static partial Regex Identifier();
}

public sealed class ModuleJobsBuilder
{
    private readonly IServiceCollection _services;

    internal ModuleJobsBuilder(JobsModule module, IServiceCollection services, JobsRegistry registry)
    {
        Module = module;
        _services = services;
        Registry = registry;
    }

    public JobsModule Module { get; }

    internal JobsRegistry Registry { get; }

    internal ModuleJobsBuilder LibraryOwned()
    {
        Module.LibraryOwned = true;
        return this;
    }

    public ModuleJobsBuilder Lane(string lane, int workers)
    {
        Module.AddLane(lane, workers);
        return this;
    }

    public ModuleJobsBuilder Retention(TimeSpan retention)
    {
        Module.SetRetention(retention);
        return this;
    }

    public ModuleJobsBuilder Command<TCommand>(string kind)
        where TCommand : notnull
    {
        Module.AddKind(new JobKind(kind, JobKindType.Command, typeof(TCommand), null, null));
        return this;
    }

    public ModuleJobsBuilder Batch<TCommand, TCheckpoint, THandler>(string kind)
        where TCommand : notnull
        where TCheckpoint : class
        where THandler : class, IBatchJobHandler<TCommand, TCheckpoint>
    {
        Module.AddKind(new JobKind(kind, JobKindType.Batch, typeof(TCommand), typeof(THandler), typeof(TCheckpoint)));
        _services.AddScoped<THandler>();
        return this;
    }

    public ModuleJobsBuilder Subscribe<TEvent, THandler>(string kind, string lane = JobOptions.DefaultLane)
        where TEvent : notnull
        where THandler : class, IEventHandler<TEvent>
    {
        if (!Module.Lanes.ContainsKey(lane))
        {
            throw new InvalidOperationException($"Module '{Module.Name}' has no job lane '{lane}'; add it with Lane(...) before Subscribe.");
        }

        if (Module.Kinds.Values.FirstOrDefault(candidate => candidate.Type == JobKindType.Rebuild && candidate.EventKind != kind
                && Module.Kinds[candidate.EventKind!].PayloadType == typeof(TEvent)) is { } rebuild)
        {
            throw new InvalidOperationException(
                $"The rebuild '{rebuild.Name}' of module '{Module.Name}' settles the deliveries of '{rebuild.EventKind}', so '{typeof(TEvent).Name}' has no other subscription there.");
        }

        Module.AddKind(new JobKind(kind, JobKindType.Event, typeof(TEvent), typeof(THandler), null, lane));
        Registry.AddSubscription(new EventSubscription(Module.Name, kind, typeof(TEvent), typeof(THandler), lane));
        _services.AddScoped<THandler>();
        return this;
    }

    public ModuleJobsBuilder Rebuild<TCommand, TEvent, THandler>(string kind)
        where TCommand : notnull
        where TEvent : notnull
        where THandler : class, IRebuildJobHandler<TCommand, TEvent>
    {
        var subscriptions = Module.Kinds.Values.Where(candidate => candidate.Type == JobKindType.Event && candidate.PayloadType == typeof(TEvent)).ToArray();
        if (subscriptions.Length != 1)
        {
            throw new InvalidOperationException(
                $"The rebuild '{kind}' needs exactly one subscription of module '{Module.Name}' to '{typeof(TEvent).Name}'; it has {subscriptions.Length}. Subscribe before Rebuild.");
        }

        var subscription = subscriptions[0];
        if (Module.Lanes[subscription.Lane!] > 1)
        {
            throw new InvalidOperationException(
                $"The rebuild '{kind}' runs on lane '{subscription.Lane}' of module '{Module.Name}', which needs one worker at most so the rebuild and the event's handler never run at once.");
        }

        Module.AddKind(new JobKind(kind, JobKindType.Rebuild, typeof(TCommand), typeof(THandler), null, subscription.Lane, subscription.Name));
        _services.AddScoped<THandler>();
        return this;
    }

    public ModuleJobsBuilder MapEvent<TDomainEvent>(Func<TDomainEvent, object?> toIntegrationEvent)
        where TDomainEvent : Claudalisk.Domain.IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(toIntegrationEvent);
        Registry.AddMapping(typeof(TDomainEvent), domainEvent => toIntegrationEvent((TDomainEvent)domainEvent));
        return this;
    }
}

public sealed class JobsRegistry
{
    private readonly Dictionary<string, JobsModule> _modules = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<EventSubscription> _subscriptions = [];
    private readonly Dictionary<Type, Func<object, object?>> _mappings = [];

    public IReadOnlyCollection<JobsModule> Modules => _modules.Values;

    public IReadOnlyList<EventSubscription> Subscriptions => _subscriptions;

    public JobsModule? Find(string nameOrSegment) =>
        _modules.TryGetValue(nameOrSegment, out var module)
            ? module
            : _modules.Values.FirstOrDefault(candidate => string.Equals(candidate.Segment, nameOrSegment, StringComparison.OrdinalIgnoreCase));

    public JobsModule ModuleFor(Type payloadType) =>
        _modules.Values.FirstOrDefault(module => module.KindFor(payloadType) is not null)
        ?? throw new InvalidOperationException(
            $"No module registers '{payloadType.FullName}' as a job. Register it with AddModuleJobs(...).Command<{payloadType.Name}>(\"<kind>\").");

    internal object? Map(object domainEvent) =>
        _mappings.TryGetValue(domainEvent.GetType(), out var map) ? map(domainEvent) : null;

    internal JobsModule GetOrAdd(string name, string schema)
    {
        if (_modules.TryGetValue(name, out var existing))
        {
            return existing.Schema == schema
                ? existing
                : throw new InvalidOperationException($"Module '{name}' already registered its jobs in schema '{existing.Schema}'.");
        }

        var module = new JobsModule(name, schema);
        _modules[name] = module;
        return module;
    }

    internal void AddSubscription(EventSubscription subscription)
    {
        if (!_subscriptions.Contains(subscription))
        {
            _subscriptions.Add(subscription);
        }
    }

    internal void AddMapping(Type domainEventType, Func<object, object?> map) => _mappings[domainEventType] = map;
}
