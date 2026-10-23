using System.Windows;

namespace XMidi;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--console"))
        {
            RunConsole().GetAwaiter().GetResult();
            return;
        }
        new Application().Run(new MainWindow());
    }

    private static async Task RunConsole()
    {
        Console.WriteLine("X-MIDI — aguardando controle Xbox. Pressione Ctrl+C para sair.");
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        var reader = new ControllerReader();
        reader.ConnectionChanged += (index, connected) =>
            Console.WriteLine($"Controle {index + 1}: {(connected ? "conectado" : "desconectado")}");
        reader.ButtonChanged += (index, button, pressed) =>
            Console.WriteLine($"Controle {index + 1}: {button} {(pressed ? "pressionado" : "liberado")}");
        reader.AnalogChanged += (index, input, x, y) =>
            Console.WriteLine(input < 2
                ? $"Controle {index + 1}: {(input == 0 ? "Analógico esquerdo" : "Analógico direito")} X={x}% Y={y}%"
                : $"Controle {index + 1}: {(input == 2 ? "LT" : "RT")} intensidade={x}%");
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                reader.Poll();
                await Task.Delay(8, cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }
}
