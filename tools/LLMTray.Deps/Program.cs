// Checks that each llama.cpp build in a runtime folder starts on a clean
// Windows: every DLL llama-server.exe and the DLLs beside it import is
// either beside them or part of Windows (adr/0001: nothing else to install).
//   dotnet run --project tools/LLMTray.Deps -- src/LLMTray.App/runtime/llama
// A PC without the Visual C++ runtime refused to start llama-server
// (0xC0000135) while CI, which has it installed, passed.
using LLMTray.Core.Runtime;

if (args.Length != 1 || !Directory.Exists(args[0]))
{
    Console.Error.WriteLine("usage: LLMTray.Deps <runtime folder with one folder per backend>");
    return 2;
}
var failed = false;
var backends = Directory.EnumerateDirectories(args[0]).Where(d => File.Exists(Path.Combine(d, "llama-server.exe"))).ToList();
if (backends.Count == 0)
{
    Console.Error.WriteLine($"no llama-server.exe under {args[0]}");
    return 1;
}
foreach (var backend in backends)
{
    try
    {
        var missing = PeImports.Missing(backend);
        Console.WriteLine($"{Path.GetFileName(backend)}: {(missing.Count == 0 ? "every DLL is there" : "missing " + string.Join(", ", missing))}");
        failed |= missing.Count > 0;
    }
    catch (InvalidDataException e)
    {
        // What a file that can't be read needs is unknown: not a pass.
        Console.WriteLine($"{Path.GetFileName(backend)}: can't be checked: {e.Message}");
        failed = true;
    }
}
return failed ? 1 : 0;
