using Microsoft.AspNetCore.Mvc;

namespace Claudalisk.AspNetCore;

internal sealed record ProblemMapping(ProblemDetails Problem, IReadOnlyDictionary<string, string> Headers);
