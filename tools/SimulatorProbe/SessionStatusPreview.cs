using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Newtonsoft.Json;
using NINA.Core.Utility.ColorSchema;
using NINA.Profile.Interfaces;
using PsfGuard.Director.Plugin.Sequencer;

namespace PsfGuard.Director.SimulatorProbe;

internal static partial class SessionUiProbe
{
    // Display-only examples in the isolated host. They never run a session or dispatch equipment.
    internal static Task RenderStatusExamplesAsync(string directory, IProfileService profiles) => Application.Current.Dispatcher.InvokeAsync(() =>
    {
        var template = Templates(Application.Current.Resources).Single(value =>
            value.DataType is Type type && type.FullName == typeof(DirectorSessionContainer).FullName);
        var exportedType = (Type)template.DataType;
        var session = Activator.CreateInstance(exportedType)!;
        var report = exportedType.GetMethod("Report", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var trigger = exportedType.GetMethod("SetTriggerActivity", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var content = (FrameworkElement)template.LoadContent();
        content.DataContext = session;
        var host = new Border { Width = 1000, Child = content };
        host.SetResourceReference(Border.BackgroundProperty, "BackgroundBrush");
        TextElement.SetFontFamily(host, Application.Current.MainWindow.FontFamily);
        TextElement.SetFontSize(host, Application.Current.MainWindow.FontSize);
        var colors = profiles.ActiveProfile.ColorSchemaSettings;
        var original = colors.ColorSchema;
        var exposing = DirectorSessionDisplay.Empty with
        {
            Phase = "Acquiring",
            Operation = "HA · 120 s exposure",
            Target = "M31",
            Connectivity = "Connected",
            Safety = "Safe"
        };
        var examples = new[]
        {
            (Name: "exposing", Display: exposing, Trigger: (string?)null),
            (Name: "autofocus", Display: exposing, Trigger: (string?)"Inject Autofocus"),
            (Name: "safety-wait", Display: exposing with
            {
                Phase = "Waiting for the safety monitor to report safe", Operation = "", Target = "-", Safety = "Unsafe"
            }, Trigger: (string?)null)
        };
        try
        {
            foreach (var palette in ColorSchemas.ReadColorSchemas().Items.Where(p => p.Name is "Persian Faint" or "Dark" or "Light"))
            {
                // Exercise NINA's actual profile-bound brushes, including changes on an open view.
                colors.ColorSchema = palette;
                foreach (var example in examples)
                {
                    report.Invoke(session, [JsonConvert.DeserializeObject(JsonConvert.SerializeObject(example.Display), report.GetParameters()[0].ParameterType)]);
                    trigger.Invoke(session, [example.Trigger]);
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                    host.Measure(new Size(host.Width, double.PositiveInfinity));
                    host.Arrange(new Rect(0, 0, host.Width, host.DesiredSize.Height));
                    host.UpdateLayout();
                    var header = Descendants(host).OfType<TextBlock>().Where(text =>
                        BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path?.Path is
                            "SessionStatus" or "CurrentDisplay.Operation" or "CurrentDisplay.Target").ToArray();
                    if (header.Length != 3 || header.Any(text => text.FontSize != Application.Current.MainWindow.FontSize
                        || text.FontFamily != Application.Current.MainWindow.FontFamily
                        || text.Foreground is not SolidColorBrush brush || brush.Color != palette.PrimaryColor))
                        throw new InvalidOperationException($"Director status did not inherit NINA's font or {palette.Name} palette.");
                    var bitmap = new RenderTargetBitmap((int)host.Width, (int)Math.Ceiling(host.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(host);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(directory, $"status-{palette.Name.Replace(' ', '-').ToLowerInvariant()}-{example.Name}.png"));
                    encoder.Save(file);
                }
            }
        }
        finally { colors.ColorSchema = original; }
    }).Task;
}
