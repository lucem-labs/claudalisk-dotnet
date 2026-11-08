# Claudalisk.Modules

`IModule`, `AddClaudaliskModules` and the module catalog. A module registers its own services and names the other modules it needs through marker classes.

## Validation

`AddClaudaliskModules` validates every module, including those from earlier calls, before it registers anything. It throws an `InvalidOperationException` naming the module type when:

- a `Contract` or a dependency is not a sealed, non-abstract class
- `Dependencies` is null, or contains a null entry
- a dependency is not provided by any module
- the dependencies form a cycle
- two different module types provide the same `Contract`, even when no module depends on it
- a module's `ConfigureServices` calls `AddClaudaliskModules`, directly or indirectly

## Behaviour to know

- If a module's `ConfigureServices` throws, the modules registered before it and its own partial services stay registered. Fail startup instead of retrying.
- The marker check accepts any sealed, non-abstract class. It does not verify that the class is empty.
- `Contract`, `Description` and `Dependencies` are default interface members. A module that derives from a base class implementing `IModule` must redeclare `IModule`, or the base must declare the members `virtual`, for the module's own properties to be seen.
