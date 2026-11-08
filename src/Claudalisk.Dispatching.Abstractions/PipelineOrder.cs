namespace Claudalisk.Dispatching;

public static class PipelineOrder
{
    public const int Logging = 1000;

    public const int Validation = 2000;

    public const int Retry = 3000;

    public const int UnitOfWork = 4000;
}
