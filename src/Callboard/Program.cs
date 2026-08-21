using Callboard.Cli;

#pragma warning disable RS0030 // Program.cs is the CLI's sole sanctioned Console access point (design.md D1 / §3 obligation 2): CommandDispatcher.Run owns the one JSON line on stdout, and BannedSymbols.txt forbids System.Console everywhere else in this project, so this is the one place the ban must be lifted rather than the one place it is trusted to be honoured.
return CommandDispatcher.Run(
    args,
    Console.Out,
    Console.In,
    Console.Error,
    Console.IsInputRedirected,
    Directory.GetCurrentDirectory());
#pragma warning restore RS0030
