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
}

internal sealed class LateHandler(LateLog log) : IHandler<LateCommand, string>
{
    public Task<string> HandleAsync(LateCommand message, CancellationToken cancellationToken)
    {
        log.Handled.Add(message.Name);
        return Task.FromResult(message.Name);
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
