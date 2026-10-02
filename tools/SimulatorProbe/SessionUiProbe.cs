using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NINA.Sequencer.SequenceItem.Utility;
using PsfGuard.Director.Plugin.Sequencer;

namespace PsfGuard.Director.SimulatorProbe;

internal static class SessionUiProbe
{
    internal static Task RenderAsync(string directory, DirectorSessionDisplay? display = null) => Application.Current.Dispatcher.InvokeAsync(() =>
    {
        // NINA gives each plugin its own AssemblyLoadContext. The test plugin's
        // reference has a different CLR identity from the actual exported type.
        var template = Templates(Application.Current.Resources).SingleOrDefault(value =>
            value.DataType is Type type && type.FullName == typeof(DirectorSessionContainer).FullName)
            ?? throw new InvalidOperationException("NINA did not discover the exported Director session template.");
        var exportedType = (Type)template.DataType;
        foreach (var width in new[] { 640, 1000 })
        {
            var session = Activator.CreateInstance(exportedType)!;
            if (display is not null)
            {
                var report = exportedType.GetMethod("Report", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var nativeDisplay = JsonConvert.DeserializeObject(JsonConvert.SerializeObject(display), report.GetParameters()[0].ParameterType);
                report.Invoke(session, [nativeDisplay]);
            }
            var beforeTarget = (NINA.Sequencer.Container.SequentialContainer)exportedType.GetProperty("BeforeNewTarget")!.GetValue(session)!;
            beforeTarget.Add(new WaitForTimeSpan { Time = 1 });
            var content = (FrameworkElement)template.LoadContent();
            content.DataContext = session;
            var host = new Border { Width = width, Background = (Brush)Application.Current.FindResource("BackgroundBrush"), Child = content };
            System.Windows.Documents.TextElement.SetForeground(host, (Brush)Application.Current.FindResource("PrimaryBrush"));
            Layout();
            var reportButton = Descendants(host).OfType<Button>().SingleOrDefault(button => Equals(button.Content, "Report equipment"))
                ?? throw new InvalidOperationException("Director equipment report button was not rendered.");
            if (reportButton.Command is null || reportButton.IsEnabled || reportButton.Style != Application.Current.FindResource("StandardButton")
                || reportButton.ActualWidth < 80)
                throw new InvalidOperationException("Equipment report button lost its native style, binding or idle-service guard.");
            var tabs = Descendants(host).OfType<TabControl>().Single();
            for (var tab = 0; tab < 4; tab++)
            {
                tabs.SelectedIndex = tab;
                Layout();
                var controls = Descendants(host).OfType<Control>()
                    .Where(control => !string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(control))).ToArray();
                if (tab == 0)
                {
                    if (controls.OfType<ComboBox>().Count() != 11 || controls.OfType<TextBox>().Count() != 10
                        || controls.OfType<CheckBox>().Count() != 9)
                        throw new InvalidOperationException($"Director session settings did not render every editor: {controls.OfType<ComboBox>().Count()} policies, {controls.OfType<TextBox>().Count()} numbers, {controls.OfType<CheckBox>().Count()} toggles.");
                    foreach (var combo in controls.OfType<ComboBox>())
                        if (combo.SelectedItem is null) throw new InvalidOperationException("Director policy selection is empty.");
                }
                if (tab == 1 && Descendants(host).OfType<Expander>().Count(expander => expander.DataContext is NINA.Sequencer.Container.SequentialContainer) < 7)
                    throw new InvalidOperationException("Director did not render all seven native instruction editors.");
                foreach (var field in controls.Where(c => c is TextBox or ComboBox or CheckBox))
                {
                    var rect = field.TransformToAncestor(host).TransformBounds(new Rect(field.RenderSize));
                    if (rect.Left < 0 || rect.Right > width + 1) throw new InvalidOperationException("Director editor escaped its layout.");
                }
                var bitmap = new RenderTargetBitmap(width, (int)Math.Ceiling(host.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(host);
                var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                if (pixels.Where((_, index) => index % 4 != 3).Distinct().Count() < 8)
                    throw new InvalidOperationException("Director session rendered a blank surface.");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(directory, $"session-{width}-{tab}.png"));
                encoder.Save(file);
            }

            void Layout()
            {
                host.Measure(new Size(width, double.PositiveInfinity));
                host.Arrange(new Rect(0, 0, width, host.DesiredSize.Height));
                host.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                host.UpdateLayout();
            }
        }
    }).Task;

    private static IEnumerable<DataTemplate> Templates(ResourceDictionary dictionary)
    {
        foreach (var key in dictionary.Keys)
            if (key is DataTemplateKey && dictionary[key] is DataTemplate template) yield return template;
        foreach (var nested in dictionary.MergedDictionaries)
            foreach (var template in Templates(nested)) yield return template;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
