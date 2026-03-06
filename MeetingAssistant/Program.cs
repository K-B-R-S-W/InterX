using System.Windows.Forms;

namespace MeetingAssistant;

/// <summary>
/// Meeting Assistant - Invisible AI meeting helper
/// Captures speech, sends to Cerebras AI, displays responses on Android device
/// </summary>
internal static class Program
{
    /// <summary>
    /// The main entry point for the application
    /// </summary>
    [STAThread]
    static void Main()
    {
        // Enable visual styles for Windows Forms
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Set up global exception handling
        Application.ThreadException += (s, e) =>
        {
            Console.WriteLine($"[Fatal] Unhandled exception: {e.Exception}");
            MessageBox.Show(
                $"An error occurred: {e.Exception.Message}\n\nThe application will continue running.",
                "Meeting Assistant Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
        };

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            Console.WriteLine($"[Fatal] Unhandled domain exception: {e.ExceptionObject}");
        };

        Console.WriteLine("===========================================");
        Console.WriteLine("    Meeting Assistant - Starting");
        Console.WriteLine("===========================================");
        Console.WriteLine();

        try
        {
            // Create and run the tray application
            var context = new TrayApplicationContext();
            Application.Run(context);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Fatal] Application failed to start: {ex.Message}");
            MessageBox.Show(
                $"Failed to start Meeting Assistant:\n\n{ex.Message}",
                "Startup Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
        }

        Console.WriteLine();
        Console.WriteLine("===========================================");
        Console.WriteLine("    Meeting Assistant - Stopped");
        Console.WriteLine("===========================================");
    }
}
