using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace Claudalisk.Dispatching;

internal sealed class ValidationStep(IServiceProvider scope) : IPipelineStep
{
    public int Order => PipelineOrder.Validation;

    public async Task<object?> InvokeAsync(MessageContext context, Func<CancellationToken, Task<object?>> next, CancellationToken cancellationToken)
    {
        var validators = scope.GetServices(typeof(IValidator<>).MakeGenericType(context.MessageType)).OfType<IValidator>()
            .Concat(scope.GetRequiredService<DispatchRegistry>().UnregisteredValidatorsFor(context.MessageType).Select(type => (IValidator)ActivatorUtilities.CreateInstance(scope, type)))
            .ToArray();
        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var validator in validators)
        {
            var result = await validator.ValidateAsync(new ValidationContext<object>(context.Message), cancellationToken);
            foreach (var failure in result.Errors)
            {
                var key = CamelCase(failure.PropertyName);
                if (!errors.TryGetValue(key, out var messages))
                {
                    errors[key] = messages = [];
                }

                messages.Add(failure.ErrorMessage);
            }
        }

        if (errors.Count > 0)
        {
            throw new MessageValidationException(context.MessageType, errors.ToDictionary(error => error.Key, error => error.Value.ToArray(), StringComparer.Ordinal));
        }

        return await next(cancellationToken);
    }

    internal static string CamelCase(string propertyName) =>
        string.Join('.', propertyName.Split('.').Select(part => part.Length == 0 ? part : char.ToLowerInvariant(part[0]) + part[1..]));
}
