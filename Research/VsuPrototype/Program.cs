using System.Text;
using PrototypeOperations = VhdStudio.Vsux.VsuxOperations;

namespace VsuPrototype;

internal static class Program {
    [STAThread]
    private static int Main(string[] args) {
        try {
            if (args.Length == 0 || (args.Length == 1 && args[0].EndsWith(".vsux", StringComparison.OrdinalIgnoreCase))) {
                ApplicationConfiguration.Initialize();
                Application.Run(new PrototypeForm(args.Length == 1 ? args[0] : null));
                return 0;
            }
            if (args is ["--self-test"]) { return PrototypeChecks.Run(); }
            if (args is ["--self-test-native"]) { return PrototypeChecks.Run(native: true); }
            if (args is ["status", var statusPath]) { Console.WriteLine(PrototypeOperations.Status(statusPath)); return 0; }
            if (args is ["protect", var source, var target]) {
                var password = ReadPassword("New password: ");
                if (password != ReadPassword("Confirm password: ")) { throw new ArgumentException("Passwords do not match."); }
                PrototypeOperations.Protect(source, target, password);
                Console.WriteLine("Protected as VSUX. Keep the .vsux and .vsux.json files together. Data is not encrypted.");
                return 0;
            }
            if (args.Length is 2 or 3 && args[0] is "attach" or "unlock" or "lock") {
                if (args.Length == 3 && (args[0] != "attach" || args[2] != "--read-only")) { throw new ArgumentException("Only attach accepts --read-only."); }
                var password = ReadPassword("Password: ");
                switch (args[0]) {
                    case "attach": PrototypeOperations.Attach(args[1], password, args.Length == 3); break;
                    case "unlock": PrototypeOperations.Unlock(args[1], password); break;
                    case "lock": PrototypeOperations.Lock(args[1], password); break;
                }
                Console.WriteLine(PrototypeOperations.Status(args[1]));
                return 0;
            }
            Console.WriteLine("VSUX experimental mount-lock prototype. Use disposable standalone VHDX images only.\n" +
                "VsuPrototype                         Open the test UI\n" +
                "VsuPrototype <image.vsux>            Open a container in the UI\n" +
                "VsuPrototype protect <disk.vhdx> <disk.vsux>\n" +
                "VsuPrototype attach <disk.vsux> [--read-only]\n" +
                "VsuPrototype lock <disk.vsux>        Safely detach, then mask the signature\n" +
                "VsuPrototype unlock <disk.vsux>      Restore signature without mounting\n" +
                "VsuPrototype status <disk.vsux>\n" +
                "VsuPrototype --self-test             Disposable structural and password checks\n" +
                "VsuPrototype --self-test-native      Real disposable VHDX attach/detach checks (Administrator)\n" +
                "Recovery: use the separate VsuRecover executable.");
            return args is ["--help"] ? 0 : 2;
        } catch (Exception ex) {
            Console.Error.WriteLine(args.Any(a => a.StartsWith("--self-test", StringComparison.Ordinal)) ? ex.ToString() : ex.Message);
            return 1;
        }
    }

    private static string ReadPassword(string prompt) {
        if (Console.IsInputRedirected) { throw new IOException("Enter passwords interactively or use the GUI. Password arguments and redirected input are not supported."); }
        Console.Write(prompt);
        var text = new StringBuilder();
        while (true) {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return text.ToString(); }
            if (key.Key == ConsoleKey.Escape) { throw new OperationCanceledException("Cancelled."); }
            if (key.Key == ConsoleKey.Backspace) { if (text.Length > 0) { text.Length--; } }
            else if (!char.IsControl(key.KeyChar) && text.Length < 1024) { text.Append(key.KeyChar); }
        }
    }
}
