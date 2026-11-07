using Claudalisk.Modules;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace Claudalisk.AspNetCore;

internal sealed class ModuleTags(IServiceProvider services, IOptions<ClaudaliskWebOptions> options)
    : IOpenApiOperationTransformer, IOpenApiDocumentTransformer
{
    public const string HostTag = "Host";
    public const string HostDescription = "Endpoints of the Host that every app has, such as health.";

    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.Description.ActionDescriptor is ControllerActionDescriptor controller)
        {
            var tag = TagFor(controller.ControllerTypeInfo.AsType());
            operation.Tags = new HashSet<OpenApiTagReference> { new(tag, context.Document) };
        }

        return Task.CompletedTask;
    }

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        var used = (document.Paths ?? [])
            .SelectMany(path => path.Value.Operations ?? [])
            .SelectMany(operation => operation.Value.Tags ?? new HashSet<OpenApiTagReference>())
            .Select(tag => tag.Name)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        var descriptions = Catalog()?.Modules.ToDictionary(module => module.Name, module => module.Description, StringComparer.Ordinal) ?? [];
        descriptions[HostTag] = HostDescription;
        foreach (var (tag, description) in options.Value.TagDescriptions)
        {
            descriptions.TryAdd(tag, description);
        }

        document.Tags = new HashSet<OpenApiTag>(
            used.Order(StringComparer.Ordinal).Select(name => new OpenApiTag
            {
                Name = name,
                Description = descriptions.GetValueOrDefault(name),
            }));
        return Task.CompletedTask;
    }

    internal string TagFor(Type controller) =>
        options.Value.TagFor?.Invoke(controller) ?? ModuleNames.ModuleOf(controller, Catalog()) ?? HostTag;

    private ModuleCatalog? Catalog() => services.GetService<ModuleCatalog>();
}
