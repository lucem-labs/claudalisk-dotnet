using Claudalisk.Modules;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Claudalisk.Dispatching.Tests.LateModule;

public sealed class LateContract;

public sealed record LateCommand(string Name);

public sealed class LateLog
{
    public List<string> Handled { get; } = [];

    public List<string> Disposed { get; } = [];
}

public sealed record LateDisposableCommand(string Name);

internal sealed class LateDisposableHandler(LateLog log) : IHandler<LateDisposableCommand, string>, IAsyncDisposable
{
    public ValueTask<string> HandleAsync(LateDisposableCommand message, CancellationToken cancellationToken) => ValueTask.FromResult(message.Name);

    public ValueTask DisposeAsync()
    {
        log.Disposed.Add("handler");
        return ValueTask.CompletedTask;
    }
}

internal sealed class LateDisposableCommandValidator(LateLog log) : AbstractValidator<LateDisposableCommand>, IDisposable
{
    public void Dispose() => log.Disposed.Add("validator");
}

internal sealed class LateHandler(LateLog log) : IHandler<LateCommand, string>
{
    public ValueTask<string> HandleAsync(LateCommand message, CancellationToken cancellationToken)
    {
        log.Handled.Add(message.Name);
        return ValueTask.FromResult(message.Name);
    }
}

internal sealed class LateCommandValidator : AbstractValidator<LateCommand>
{
    public LateCommandValidator() => RuleFor(command => command.Name).NotEmpty();
}

public sealed class LateModule : IModule
{
    public Type Contract => typeof(LateContract);

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration) => services.AddSingleton<LateLog>();
}
