using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("Naming", "CA1716:Identifiers should not match keywords",
    Justification = "'Error' is the domain term used by every block; C# and F# consumers are unaffected.",
    Scope = "type", Target = "~T:Agentic.Blocks.Core.Error")]
[assembly: SuppressMessage("Naming", "CA1716:Identifiers should not match keywords",
    Justification = "'Error' is the domain term used by every block.",
    Scope = "member", Target = "~P:Agentic.Blocks.Core.Result.Error")]
