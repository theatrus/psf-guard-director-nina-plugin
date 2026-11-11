using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PsfGuard.Director.Plugin;
using PsfGuard.Director.Plugin.Sequencer;
using PsfGuard.Director.Runtime;
using Xunit;

namespace PsfGuard.Director.Tests;

[CollectionDefinition("WPF settings rendering", DisableParallelization = true)]
public sealed class WpfSettingsRenderingCollection;

[Collection("WPF settings rendering")]
public sealed class SettingsViewTests
{
    [Fact]
    public async Task RealOptionsTemplateHasNinaStylesAndResponsiveLayout()
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
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        // WPF's pack resource loader needs an application resource scope. This
        // test uses NINA's actual themes. Keep the process-wide Application
        // exclusive: native workflow fixtures can capture its dispatcher.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
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
        resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/NINA.WPF.Base;component/Resources/Styles/CheckBox.xaml")
        });
        resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/NINA.WPF.Base;component/Resources/Styles/TextBlock.xaml")
        });
        try
        {
            var dictionary = new Options();
            var template = (DataTemplate)dictionary["PSF Guard Director_Options"];
            CheckCommandRefreshFromWorker();
            CheckCoordinatorPairButton(template);
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
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                    var advanced = Descendants(host).OfType<Expander>().Single(e => Equals(e.Header, "Advanced and diagnostics"));
                    Assert.False(advanced.IsExpanded);
                    Assert.Contains(Descendants(host).OfType<TextBlock>(), text => text.Text == model.EquipmentReportStatus);
                    var status = Assert.Single(Descendants(host).OfType<TextBlock>(), text => text.Text == model.Overview.Phase);
                    var inheritedFontSize = System.Windows.Documents.TextElement.GetFontSize(host);
                    Assert.Equal(inheritedFontSize, status.FontSize);
                    Assert.Same(resources["PrimaryBrush"], status.Foreground);
                    System.Windows.Documents.TextElement.SetFontSize(host, 14);
                    Assert.Equal(14, status.FontSize);
                    System.Windows.Documents.TextElement.SetFontSize(host, inheritedFontSize);
                    // A profile/palette change must also update an already-open status view.
                    var alternateText = new SolidColorBrush(Colors.DarkRed);
                    host.Resources["PrimaryBrush"] = alternateText;
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                    Assert.Same(alternateText, status.Foreground);
                    host.Resources.Remove("PrimaryBrush");
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                    Assert.Same(resources["PrimaryBrush"], status.Foreground);
                    Assert.DoesNotContain(Descendants(host).OfType<TextBlock>(), text => text.Text == "Not armed");
                    var buttons = Descendants(host).OfType<Button>().ToArray();
                    Assert.Equal(4, buttons.Length);
                    Assert.DoesNotContain(buttons, button => button.Content is TextBlock { Text: "Start runtime" });
                    foreach (var button in buttons)
                    {
                        Assert.NotNull(button.Command);
                        Assert.Same(resources["StandardButton"], button.Style);
                        var label = Assert.IsType<TextBlock>(button.Content);
                        Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(label.Foreground).Color);
                        Assert.True(button.ActualWidth >= label.ActualWidth + 20);
                        var bounds = button.TransformToAncestor(host).TransformBounds(new Rect(button.RenderSize));
                        Assert.True(bounds.Right <= width && bounds.Left >= 0);
                    }
                    var options = Descendants(host).OfType<CheckBox>().ToArray();
                    Assert.Equal(new[] { "Allow unencrypted HTTP", "Send Target Scheduler compatible events", "Send star-detection settings to PSF Guard", "Report sequence activity to PSF Guard", "Upload captured frames to PSF Guard" },
                        options.Select(option => Assert.IsType<string>(option.Content)));
                    var schedulerEvents = options.Single(option => (string)option.Content == "Send Target Scheduler compatible events");
                    Assert.False(schedulerEvents.IsChecked);
                    schedulerEvents.SetCurrentValue(CheckBox.IsCheckedProperty, true);
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                    Assert.True(model.EmitTargetSchedulerEvents);
                    schedulerEvents.SetCurrentValue(CheckBox.IsCheckedProperty, false);
                    var uploads = options.Single(option => (string)option.Content == "Upload captured frames to PSF Guard");
                    Assert.False(uploads.IsChecked);
                    uploads.SetCurrentValue(CheckBox.IsCheckedProperty, true);
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                    Assert.True(model.UploadCapturedFrames);
                    uploads.SetCurrentValue(CheckBox.IsCheckedProperty, false);
                    foreach (var option in options)
                    {
                        var label = Assert.Single(Descendants(option).OfType<TextBlock>(), text => text.Text == (string)option.Content);
                        Assert.True(label.Visibility == Visibility.Visible && label.ActualWidth > 0 && label.ActualHeight > 0,
                            $"The NINA theme must not hide '{option.Content}'.");
                        Assert.Same(resources["PrimaryBrush"], label.Foreground);
                        var bounds = label.TransformToAncestor(host).TransformBounds(new Rect(label.RenderSize));
                        Assert.True(bounds.Right <= width && bounds.Left >= 0);
                    }
                    var bitmap = new RenderTargetBitmap(width, (int)Math.Ceiling(host.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    var drawing = new DrawingVisual();
                    using (var context = drawing.RenderOpen())
                        context.DrawRectangle(new VisualBrush(host), null, new Rect(0, 0, width, host.ActualHeight));
                    bitmap.Render(drawing);
                    var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                    bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                    Assert.True(pixels.Where((_, index) => index % 4 != 3).Count(value => value > 150) > 1000,
                        "Settings render must contain visible controls and text.");
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    var path = Path.Combine(FindRoot(), "artifacts", $"settings-{width}-{(state.Length > 10 ? "fault" : state.ToLowerInvariant())}.png");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using var file = File.Create(path);
                    encoder.Save(file);

                    advanced.IsExpanded = true;
                    host.Measure(new Size(width, double.PositiveInfinity));
                    host.Arrange(new Rect(0, 0, width, host.DesiredSize.Height));
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                    var collaboration = Descendants(advanced).OfType<Expander>().Single();
                    Assert.False(collaboration.IsExpanded);
                    var diagnosticButtons = Descendants(advanced).OfType<Button>().ToArray();
                    Assert.Equal(2, diagnosticButtons.Length);
                    Assert.Equal(state != "Ready", diagnosticButtons[0].IsEnabled);
                    Assert.Equal(state != "Stopped", diagnosticButtons[1].IsEnabled);
                    collaboration.IsExpanded = true;
                    host.Measure(new Size(width, double.PositiveInfinity));
                    host.Arrange(new Rect(0, 0, width, host.DesiredSize.Height));
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                    var collaborationButtons = Descendants(collaboration).OfType<Button>().ToArray();
                    Assert.Equal(7, collaborationButtons.Length);
                    Assert.All(collaborationButtons, button => Assert.NotNull(button.Command));
                    var collaborationInputs = Descendants(collaboration).OfType<TextBox>().ToArray();
                    Assert.Equal(3, collaborationInputs.Length);
                    Assert.Contains(Descendants(collaboration).OfType<CheckBox>(),
                        option => Equals(option.Content, "Allow loopback HTTP for isolated testing"));
                }
        }
        finally
        {
            app.Shutdown();
            // Shutdown queues its cleanup on the dispatcher. Drain it before
            // exiting this thread so native collections cannot retain a dead UI.
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
        Assert.Null(Application.Current);
    }

    private static void CheckCommandRefreshFromWorker()
    {
        var enabled = true;
        var command = new AsyncCommand(() => Task.CompletedTask, () => enabled, error => throw error);
        var button = new Button { Command = command };
        Assert.True(button.IsEnabled);
        var notifications = 0;
        command.CanExecuteChanged += (_, _) =>
        {
            Assert.True(button.Dispatcher.CheckAccess());
            notifications++;
        };
        enabled = false;
        Task.Run(command.Refresh).GetAwaiter().GetResult();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        Assert.False(button.IsEnabled);
        Assert.True(notifications > 0);
    }

    private static void CheckCoordinatorPairButton(DataTemplate template)
    {
        var profile = Guid.NewGuid();
        var url = "";
        var binding = new CoordinatorBinding(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), profile);
        var exchanges = 0;
        var code = "psfdpt_" + new string('a', 64);
        using var connection = new DirectorConnection(() => profile, () => true, () => url, value => url = value,
            (_, _) => null, (_, _) => { }, exchange: (endpoint, submittedCode, _, _, _) =>
            {
                exchanges++;
                Assert.Equal("https://psf-guard.example/", endpoint.AbsoluteUri);
                Assert.Equal(code, submittedCode);
                return Task.FromResult(new CoordinatorPairing(binding, Guid.NewGuid(), "test-credential"));
            });
        var content = (FrameworkElement)template.LoadContent();
        content.DataContext = new ViewModel("Stopped") { Connection = connection };
        content.Measure(new Size(640, double.PositiveInfinity));
        content.Arrange(new Rect(content.DesiredSize));
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        var inputs = Descendants(content).OfType<TextBox>().Where(input => ReferenceEquals(input.DataContext, connection)).ToArray();
        Assert.Equal(2, inputs.Length);
        var pair = Descendants(content).OfType<Button>().First(button => button.Content is TextBlock { Text: "Pair" });
        Assert.Same(connection.PairCommand, pair.Command);
        Assert.False(pair.IsEnabled);
        var click = Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(pair).GetPattern(PatternInterface.Invoke));
        var syncCode = "psfpt_" + new string('a', 64);
        inputs[1].SetCurrentValue(TextBox.TextProperty, syncCode);
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        Assert.True(pair.IsEnabled);
        click.Invoke();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        Assert.Contains("PSF Guard Sync code", connection.PairingStatus);
        Assert.Equal(syncCode, inputs[1].Text);
        Assert.Equal(0, exchanges);

        inputs[1].SetCurrentValue(TextBox.TextProperty, code);
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        click.Invoke();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        Assert.Contains("valid PSF Guard server URL", connection.PairingStatus);
        Assert.Equal(code, inputs[1].Text);
        Assert.Equal(0, exchanges);

        inputs[0].SetCurrentValue(TextBox.TextProperty, "https://psf-guard.example/");
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        Assert.True(pair.IsEnabled);
        click.Invoke();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        Assert.Equal(1, exchanges);
        Assert.StartsWith("Paired to rig", connection.PairingStatus);
        Assert.Empty(inputs[1].Text);
        Assert.False(pair.IsEnabled);
    }

    private static void TextElementForeground(DependencyObject host) =>
        System.Windows.Documents.TextElement.SetForeground(host, Brushes.White);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is UIElement { Visibility: not Visibility.Visible }) continue;
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
        public string EquipmentReportStatus => "Equipment report accepted as rig configuration revision 2.";
        public object Connection { get; init; } = new ConnectionViewModel();
        public object Collaboration { get; } = new CollaborationViewModel();
        public string ProfileName => "Director simulator - long profile name";
        public string RuntimeStatus => state;
        public string EngineVersion => "0.2.0";
        public string AcquisitionStatus => "Not armed";
        public DirectorSessionDisplay Overview => DirectorSessionDisplay.Empty with
        {
            Phase = state == "Ready" ? "Running N.I.N.A. trigger: Inject Autofocus" : state == "Stopped" ? "No sequence running" : "Waiting for the safety monitor",
            Operation = state == "Ready" ? "Inject Autofocus" : "Waiting for the next instruction",
            Target = "M31 — Andromeda galaxy",
            Safety = state == "Ready" ? "Safe" : "Monitor disconnected",
            WaitReason = state.Length > 10 ? "Connect the configured safety monitor, or choose attended operation with no monitor selected." : "-"
        };
        public ICommand StartRuntimeCommand { get; } = new StubCommand(state != "Ready");
        public ICommand StopRuntimeCommand { get; } = new StubCommand(state != "Stopped");
        public ICommand CheckInCommand { get; } = new StubCommand(state != "Ready");
        public ICommand CancelCheckInCommand { get; } = new StubCommand(false);
        public string CheckInStatus => "Checked in 128 runs; 16000 events; 4 workloads released";
        public bool SendStarDetectionSettings { get; set; } = true;
        public bool ReportSequenceActivity { get; set; } = true;
        public bool EmitTargetSchedulerEvents { get; set; }
        public bool UploadCapturedFrames { get; set; }
        public string UploadStatus => "File uploads are off. Queued files wait; image record delivery is unchanged.";
        public string UploadQueueStatus => "2 frames are queued; PSF Guard refused 1 frame.";
        public ICommand RetryRefusedUploadsCommand { get; } = new StubCommand(true);
        public ICommand ForgetHeldUploadsCommand { get; } = new StubCommand(true);
    }

    public sealed class ConnectionViewModel
    {
        public string ServerUrl { get; set; } = "https://psf-guard.example";
        public string PairingCode { get; set; } = "";
        public string PairingStatus => "Paired to rig 9f8d73a9-8055-42a6-a105-fb74fe15a398";
        public bool AllowInsecureHttp { get; set; }
        public bool IsEditable => true;
        public ICommand PairCommand { get; } = new StubCommand(false);
        public ICommand ResetPairingCommand { get; } = new StubCommand(true);
    }
    public sealed class CollaborationViewModel
    {
        public string ServerUrl { get; set; } = "https://collaboration.example/community/";
        public string RigName { get; set; } = "Director simulator";
        public string PairingCode { get; set; } = "";
        public string Status => "Registered as 000000000001";
        public bool IsEditable => true;
        public bool AllowLoopbackHttp { get; set; }
        public ICommand PairCommand { get; } = new StubCommand(false);
        public ICommand SigninCommand { get; } = new StubCommand(true);
        public ICommand OpenSigninCommand { get; } = new StubCommand(false);
        public ICommand PollCommand { get; } = new StubCommand(false);
        public ICommand ValidateCommand { get; } = new StubCommand(true);
        public ICommand DisconnectCommand { get; } = new StubCommand(true);
        public ICommand NewConnectionCommand { get; } = new StubCommand(true);
    }

    private sealed class StubCommand(bool enabled) : ICommand
    {
        public bool CanExecute(object? parameter) => enabled;
        public void Execute(object? parameter) { }
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
