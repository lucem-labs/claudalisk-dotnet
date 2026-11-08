using Microsoft.Extensions.Options;

namespace Claudalisk.Scheduling;

internal sealed class SchedulingOptionsValidator(SchedulableCommands commands) : IValidateOptions<SchedulingOptions>
{
    public ValidateOptionsResult Validate(string? name, SchedulingOptions options)
    {
        var problems = new List<string>();
        if (options.TickInterval <= TimeSpan.Zero)
        {
            problems.Add("SchedulingOptions.TickInterval must be positive.");
        }

        try
        {
            Schedule.From(options, commands);
        }
        catch (InvalidOperationException exception)
        {
            problems.Add(exception.Message);
        }

        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}
