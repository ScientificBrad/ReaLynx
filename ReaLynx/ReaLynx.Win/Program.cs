namespace ReaLynx.Win;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        string? bootRom = null;
        string? cart = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--boot" && i + 1 < args.Length)
                bootRom = args[++i];
            else if (!args[i].StartsWith('-'))
                cart = args[i];
        }

        Application.Run(new MainForm(bootRom, cart));
    }
}
