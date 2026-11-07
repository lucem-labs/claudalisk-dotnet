using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("Naming", "CA1711", Scope = "type", Target = "~T:Claudalisk.Jobs.IJobQueue", Justification = "IJobQueue is the name CLA-0058 and the jobs issue give the queue that jobs are enqueued on.")]
[assembly: SuppressMessage("Naming", "CA1711", Scope = "type", Target = "~T:Claudalisk.Jobs.IEventHandler`1", Justification = "It handles domain and integration events (CLA-0068), not .NET events, so the EventHandler suffix names what it is.")]
[assembly: SuppressMessage("Naming", "CA1716", Scope = "type", Target = "~T:Claudalisk.Jobs.IJobContext", Justification = "Module and step are the job's module and progress step; the packages target C#, where they are not keywords.")]
[assembly: SuppressMessage("Naming", "CA1716", Scope = "type", Target = "~T:Claudalisk.Jobs.IJobTransport", Justification = "module is the job's module and step its progress step; the packages target C#, where they are not keywords.")]
[assembly: SuppressMessage("Naming", "CA1716", Scope = "type", Target = "~T:Claudalisk.Jobs.IJobWriter", Justification = "module is the job's module; the packages target C#, where it is not a keyword.")]
