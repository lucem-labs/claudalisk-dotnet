namespace Claudalisk.Scheduling;

public sealed record SchedulableCommand(string Name, string Module, Type CommandType);

internal sealed class SchedulableCommands
{
    private readonly Dictionary<string, SchedulableCommand> _commands = new(StringComparer.Ordinal);

    public IReadOnlyCollection<SchedulableCommand> All => _commands.Values;

    public SchedulableCommand? Find(string name) => _commands.GetValueOrDefault(name);

    internal void Add(SchedulableCommand command)
    {
        if (_commands.TryGetValue(command.Name, out var existing) && existing != command)
        {
            throw new InvalidOperationException(
                $"'{command.Name}' is already a schedulable command of module '{existing.Module}' ({existing.CommandType.FullName}).");
        }

        _commands[command.Name] = command;
    }
}
