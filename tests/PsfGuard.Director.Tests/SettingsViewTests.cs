using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PsfGuard.Director.Plugin;
using Xunit;

namespace PsfGuard.Director.Tests;

public sealed class SettingsViewTests
{
    [Fact]
    public async Task RealOptionsTemplateRendersWithNinaButtonStyle()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { RenderStates(); completion.SetResult(); }
            catch (Exception error) { completion.SetException(error); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static void RenderStates()
    {
        // WPF's pack resource loader needs an application resource scope. This
        // test uses NINA's actual button template, not a browser approximation.
        var app = new Application();
        var resources = app.Resources;
        foreach (var key in new[] { "BackgroundBrush", "SecondaryBackgroundBrush", "TertiaryBackgroundBrush", "BorderBrush" })
            resources[key] = new SolidColorBrush(Color.FromRgb(32, 32, 32));
        resources["PrimaryBrush"] = Brushes.White;
        resources["ButtonBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(65, 65, 65));
        resources["ButtonBackgroundSelectedBrush"] = new SolidColorBrush(Color.FromRgb(90, 90, 90));
        resources["ButtonForegroundBrush"] = Brushes.White;
        resources["ButtonForegroundDisabledBrush"] = Brushes.Gray;
        resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/NINA.WPF.Base;component/Resources/Styles/Button.xaml")
        });
        var dictionary = new Options();
        var template = (DataTemplate)dictionary["PSF Guard Director_Options"];
        foreach (var width in new[] { 240, 280, 360, 640 })
            foreach (var state in new[] { "Stopped", "Ready", "Runtime verification or protocol failed. Reinstall the matching Director bundle." })
            {
                var model = new ViewModel(state);
                var content = (FrameworkElement)template.LoadContent();
                content.DataContext = model;
                var host = new Border
                {
                    Width = width,
                    Padding = new Thickness(16),
                    Background = Brushes.Black,
                    Child = content
                };
                TextElementForeground(host);
                host.Measure(new Size(width, double.PositiveInfinity));
                host.Arrange(new Rect(0, 0, width, host.DesiredSize.Height));
                host.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                host.UpdateLayout();
                var buttons = Descendants(host).OfType<Button>().ToArray();
                Assert.Equal(2, buttons.Length);
                Assert.Equal(state != "Ready", buttons[0].IsEnabled);
                Assert.Equal(state != "Stopped", buttons[1].IsEnabled);
                foreach (var button in buttons)
                {
                    Assert.Same(resources["StandardButton"], button.Style);
                    var label = Assert.IsType<TextBlock>(button.Content);
                    Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(label.Foreground).Color);
                    Assert.True(button.ActualWidth >= label.ActualWidth + 20);
                    var bounds = button.TransformToAncestor(host).TransformBounds(new Rect(button.RenderSize));
                    Assert.True(bounds.Right <= width && bounds.Left >= 0);
                }
                var bitmap = new RenderTargetBitmap(width, (int)Math.Ceiling(host.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(host);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var path = Path.Combine(FindRoot(), "artifacts", $"settings-{width}-{(state.Length > 10 ? "fault" : state.ToLowerInvariant())}.png");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var output = File.Create(path);
                encoder.Save(output);
            }
        app.Shutdown();
    }

    private static void TextElementForeground(DependencyObject host) =>
        System.Windows.Documents.TextElement.SetForeground(host, Brushes.White);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static string FindRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "runtime.lock.json"))) root = root.Parent;
        return root?.FullName ?? throw new IOException("Repository not found.");
    }

    public sealed class ViewModel(string state)
    {
        public string ProfileName => "Director simulator - long profile name";
        public string RuntimeStatus => state;
        public string EngineVersion => "0.2.0";
        public string AcquisitionStatus => "Not armed";
        public ICommand StartRuntimeCommand { get; } = new StubCommand(state != "Ready");
        public ICommand StopRuntimeCommand { get; } = new StubCommand(state != "Stopped");
    }

    private sealed class StubCommand(bool enabled) : ICommand
    {
        public bool CanExecute(object? parameter) => enabled;
        public void Execute(object? parameter) { }
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
