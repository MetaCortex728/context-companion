namespace ContextCompanion.Tool;

/// <summary>The <c>ccomp</c> command line. Commands are added as Stage 1 progresses.</summary>
public static class Cli
{
    public const int Ok = 0, Failed = 1, Usage = 2;

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            output.WriteLine("usage: ccomp <command> [options]");
            return args.Length == 0 ? Usage : Ok;
        }
        error.WriteLine($"ccomp: unknown command '{args[0]}'. Run 'ccomp --help'.");
        return Usage;
    }
}
