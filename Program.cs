namespace TailwindRemapper;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--self-test"))
        {
            try { SelfTest.Run(); Environment.ExitCode = 0; }
            catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "self-test.txt"), ex.ToString()); Environment.ExitCode = 1; }
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(args.Contains("--mcdu") ? new McduForm(args.Contains("--start")) : new MainForm());
    }
}
